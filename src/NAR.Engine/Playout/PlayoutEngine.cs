using System.Diagnostics;
using NAR.Core.Config;
using NAR.Core.Media;
using NAR.Core.Scheduling;

namespace NAR.Engine.Playout;

/// <summary>
/// Le moteur d'une chaîne. Une boucle cadencée par une horloge maîtresse produit exactement
/// une image (et son audio) par période, quoi qu'il arrive : source en cours, bouche-trou,
/// image figée en cas de retard, ou mire. Le conducteur décide, le moteur exécute.
/// </summary>
public sealed class PlayoutEngine(
    ChannelConfig config,
    MediaProbe probe,
    EngineStatus status,
    ILoggerFactory loggerFactory) : BackgroundService
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<PlayoutEngine>();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Laisse Kestrel démarrer avant d'occuper le processeur.
        await Task.Yield();
        try
        {
            await RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Le moteur s'est arrêté sur une erreur");
            throw;
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var format = new ChannelFormat(config.Format.Width, config.Format.Height, FrameRate.Parse(config.Format.FrameRate));
        var rate = format.Rate;
        var fps = rate.NominalFps;
        var asRun = new AsRunLog(config.Resolve(config.AsRunDirectory), config.Name, _logger);

        _logger.LogInformation("Chaîne {Name} : format {Format}, {Count} élément(s) au conducteur",
            config.Name, format, config.Playlist.Items.Count);

        // 1. Analyse des médias (en phase 1, ce sera fait une fois pour toutes à l'ingest).
        var probes = new ProbeResult[config.Playlist.Items.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, probes.Length),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            async (i, token) =>
            {
                probes[i] = await probe.ProbeAsync(config.Resolve(config.Playlist.Items[i].Path), token);
                if (!probes[i].Ok)
                    _logger.LogWarning("Élément {Index} « {Title} » inutilisable : {Error}",
                        i, config.Playlist.Items[i].Title, probes[i].Error);
            });

        ProbeResult? fillerProbe = null;
        if (!string.IsNullOrWhiteSpace(config.Filler))
        {
            fillerProbe = await probe.ProbeAsync(config.Resolve(config.Filler!), ct);
            if (!fillerProbe.Ok) _logger.LogWarning("Bouche-trou inutilisable ({Error}) : la mire prendra le relais", fillerProbe.Error);
        }

        // 2. Époque du moteur : l'image 0 sera diffusée à cet instant précis. On se laisse 2 s pour
        //    lancer l'encodeur et le pré-roll, afin que l'horloge des images colle à l'heure murale.
        var epoch = DateTimeOffset.Now.AddSeconds(2);
        var items = BuildSchedule(probes, rate, epoch);
        var conductor = new Conductor(items, config.Playlist.Loop, rate.FramesFloor(TimeSpan.FromSeconds(config.PreloadSeconds)));

        // 3. Sortie continue.
        var encoder = new OutputEncoder(config, format, loggerFactory.CreateLogger<OutputEncoder>());
        encoder.Start();
        var lastEncoderStart = Stopwatch.GetTimestamp();
        long encoderRestarts = 0;

        var slate = Slate.CreateColorBars(format);
        var video = new byte[format.VideoFrameBytes];
        var audio = new byte[format.MaxAudioBytesPerFrame];
        Buffer.BlockCopy(slate, 0, video, 0, slate.Length);

        SourceDecoder? current = null;
        ScheduledItem? currentItem = null;
        SourceDecoder? filler = null;
        var fillerRetryAt = 0L;
        var preloads = new Dictionary<int, SourceDecoder>();
        var state = AntennaState.Starting;
        long underrun = 0, underrunTotal = 0, sourceFailures = 0;
        string? lastIncident = null;
        var lastLateWarning = 0L;

        var clock = Stopwatch.StartNew();
        long frame = 0;

        SourceDecoder CreateDecoder(ScheduledItem item, long offset)
        {
            var decoder = new SourceDecoder($"#{item.Index} {item.Title}", item.Path, item.InFrames + offset,
                item.DurationFrames - offset, loop: false, item.GainDb, probes[item.Index], format,
                config.FfmpegPath, loggerFactory.CreateLogger<SourceDecoder>(), bufferFrames: fps);
            decoder.Start();
            return decoder;
        }

        void EndCurrent(string evt)
        {
            if (current is null || currentItem is null) return;
            if (current.Failure is not null)
            {
                sourceFailures++;
                lastIncident = $"{DateTimeOffset.Now:HH:mm:ss} « {currentItem.Title} » : {current.Failure}";
                asRun.Write("error", frame, rate.ToTimecode(frame), currentItem.Id, currentItem.Title, current.Delivered, current.Failure);
            }
            asRun.Write(evt, frame, rate.ToTimecode(frame), currentItem.Id, currentItem.Title, current.Delivered);
            var old = current;
            _ = Task.Run(() => old.DisposeAsync().AsTask());
            current = null;
            currentItem = null;
        }

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // --- Décision du conducteur -------------------------------------------------
                // Si l'élément démarré est inutilisable sur-le-champ (fichier corrompu…), on redemande
                // une décision dans la même image : aucune image n'est perdue.
                for (var attempt = 0; attempt < 4; attempt++)
                {
                    var decision = conductor.Tick(frame, current is null || current.IsFinished);

                    if (decision.PreloadIndex >= 0 && !preloads.ContainsKey(decision.PreloadIndex))
                    {
                        var item = items[decision.PreloadIndex];
                        _logger.LogInformation("Préchargement de « {Title} »", item.Title);
                        preloads[decision.PreloadIndex] = CreateDecoder(item, 0);
                    }

                    switch (decision.Action)
                    {
                        case ConductorAction.Start:
                        {
                            EndCurrent(current?.IsFinished == false ? "cut" : "end");
                            var item = items[decision.ItemIndex];
                            if (preloads.Remove(decision.ItemIndex, out var ready) && decision.OffsetFrames == 0)
                            {
                                current = ready;
                            }
                            else
                            {
                                if (ready is not null) _ = Task.Run(() => ready.DisposeAsync().AsTask());
                                current = CreateDecoder(item, decision.OffsetFrames);
                            }
                            currentItem = item;
                            underrun = 0;
                            asRun.Write("start", frame, rate.ToTimecode(frame), item.Id, item.Title,
                                detail: decision.OffsetFrames > 0 ? $"pris en cours (+{rate.ToTimecode(decision.OffsetFrames)})" : null);
                            _logger.LogInformation("{Tc} ▶ « {Title} »{Offset}", rate.ToTimecode(frame), item.Title,
                                decision.OffsetFrames > 0 ? $" (pris à +{rate.ToTimecode(decision.OffsetFrames)})" : "");

                            // Les préchargements qui ne servent plus sont libérés.
                            foreach (var key in preloads.Keys.Where(k => k != conductor.NextIndex).ToList())
                            {
                                var stale = preloads[key];
                                preloads.Remove(key);
                                _ = Task.Run(() => stale.DisposeAsync().AsTask());
                            }
                            break;
                        }
                        case ConductorAction.Fallback:
                            EndCurrent("end");
                            asRun.Write("fallback", frame, rate.ToTimecode(frame));
                            _logger.LogWarning("{Tc} Rien de prévu : passage en secours", rate.ToTimecode(frame));
                            break;
                    }
                    if (decision.Action != ConductorAction.Start || current is null || !current.IsFinished) break;
                }

                // Bouche-trou à la demande quand on est en secours.
                if (current is null && filler is null && fillerProbe is { Ok: true } && clock.ElapsedMilliseconds >= fillerRetryAt)
                {
                    filler = new SourceDecoder("bouche-trou", config.Resolve(config.Filler!), 0, long.MaxValue, loop: true, 0,
                        fillerProbe, format, config.FfmpegPath, loggerFactory.CreateLogger<SourceDecoder>(), bufferFrames: fps);
                    filler.Start();
                }
                else if (current is not null && filler is not null)
                {
                    var f = filler;
                    filler = null;
                    _ = Task.Run(() => f.DisposeAsync().AsTask());
                }

                // Pré-roll : au tout premier démarrage, on attend que la première source ait une image
                // (5 s maximum) avant de lancer l'horloge, pour ne pas commencer sur une image figée.
                if (frame == 0)
                {
                    var first = current ?? filler;
                    var waited = Stopwatch.StartNew();
                    while (first is not null && first.Buffered == 0 && !first.IsFinished && waited.ElapsedMilliseconds < 5000)
                        await Task.Delay(20, ct);
                    var untilEpoch = epoch - DateTimeOffset.Now;
                    if (untilEpoch > TimeSpan.Zero) await Task.Delay(untilEpoch, ct);
                    else _logger.LogWarning("Démarrage plus lent que prévu : l'antenne a {Late:0} ms de retard sur l'heure", -untilEpoch.TotalMilliseconds);
                    clock.Restart();
                }

                // --- Production de l'image ------------------------------------------------
                var audioBytes = format.AudioBytesForFrame(frame);
                var source = current ?? filler;

                if (source is not null && ReadFrame(source, video, audio, audioBytes))
                {
                    state = current is not null ? AntennaState.Playing : AntennaState.Filler;
                    underrun = 0;
                }
                else
                {
                    Array.Clear(audio, 0, audioBytes);
                    if (source is not null && !source.IsFinished)
                    {
                        // Image pas encore prête : on fige la précédente (le tampon vidéo n'a pas bougé).
                        state = AntennaState.Hold;
                        underrun++;
                        underrunTotal++;
                        if (underrun > fps * 2)
                        {
                            source.Fail("aucune image reçue depuis 2 s");
                            underrun = 0;
                        }
                    }
                    else if (source is not null && ReferenceEquals(source, filler))
                    {
                        // Le bouche-trou lui-même a lâché : mire, et nouvel essai dans 30 s.
                        lastIncident = $"{DateTimeOffset.Now:HH:mm:ss} bouche-trou : {filler.Failure ?? "terminé"}";
                        var f = filler;
                        filler = null;
                        fillerRetryAt = clock.ElapsedMilliseconds + 30_000;
                        _ = Task.Run(() => f.DisposeAsync().AsTask());
                    }
                    else if (source is null)
                    {
                        if (state != AntennaState.Slate) Buffer.BlockCopy(slate, 0, video, 0, slate.Length);
                        state = AntennaState.Slate;
                    }
                    // source en cours terminée : le conducteur enchaîne à l'image suivante (image figée d'ici là).
                }

                // --- Envoi à l'encodeur ----------------------------------------------------
                if (encoder.HasFailed && Stopwatch.GetElapsedTime(lastEncoderStart) > TimeSpan.FromSeconds(5))
                {
                    lastIncident = $"{DateTimeOffset.Now:HH:mm:ss} encodeur : {encoder.Failure ?? "arrêté"} — redémarrage";
                    _logger.LogError("Encodeur en échec ({Failure}), redémarrage", encoder.Failure);
                    var old = encoder;
                    _ = Task.Run(() => old.DisposeAsync().AsTask());
                    encoder = new OutputEncoder(config, format, loggerFactory.CreateLogger<OutputEncoder>());
                    encoder.Start();
                    lastEncoderStart = Stopwatch.GetTimestamp();
                    encoderRestarts++;
                }
                if (!encoder.HasFailed)
                    await encoder.SendAsync(video, audio.AsMemory(0, audioBytes), ct);

                // --- Statut ------------------------------------------------------------------
                var target = rate.TimeFromFrames(frame + 1);
                var lateMs = (clock.Elapsed - target).TotalMilliseconds;
                if (frame % Math.Max(1, fps / 2) == 0)
                {
                    var next = conductor.NextIndex;
                    status.Publish(new EngineStatusSnapshot(
                        config.Name, format.ToString(), state.ToString(), frame, rate.ToTimecode(frame), epoch,
                        currentItem?.Title, currentItem?.Id,
                        currentItem is null ? null : Math.Max(0, (conductor.CurrentEndFrame - frame) / rate.FramesPerSecond),
                        next >= 0 ? items[next].Title : null,
                        Math.Round(lateMs, 1), encoder.QueuedFrames, underrunTotal, sourceFailures, encoderRestarts, lastIncident));
                }
                if (lateMs > 1000 && clock.ElapsedMilliseconds - lastLateWarning > 10_000)
                {
                    lastLateWarning = clock.ElapsedMilliseconds;
                    _logger.LogWarning("La chaîne a {Late:0} ms de retard sur l'horloge : processeur saturé ?", lateMs);
                }

                // --- Horloge maîtresse ---------------------------------------------------------
                frame++;
                var wait = target - clock.Elapsed;
                if (wait > TimeSpan.FromMilliseconds(1))
                    await Task.Delay(wait, ct);
            }
        }
        finally
        {
            EndCurrent("stop");
            foreach (var p in preloads.Values) await p.DisposeAsync();
            if (filler is not null) await filler.DisposeAsync();
            await encoder.DisposeAsync();
            asRun.Write("shutdown", frame, rate.ToTimecode(frame));
            _logger.LogInformation("Moteur arrêté à {Tc}", rate.ToTimecode(frame));
        }
    }

    private static bool ReadFrame(SourceDecoder source, byte[] video, byte[] audio, int audioBytes) =>
        source.TryReadFrame(video, audio.AsSpan(0, audioBytes));

    private List<ScheduledItem> BuildSchedule(ProbeResult[] probes, FrameRate rate, DateTimeOffset epoch)
    {
        var list = new List<ScheduledItem>();
        for (var i = 0; i < config.Playlist.Items.Count; i++)
        {
            var it = config.Playlist.Items[i];
            var inPoint = ItemConfig.ParseTime(it.In) ?? TimeSpan.Zero;
            TimeSpan duration;
            if (ItemConfig.ParseTime(it.Duration) is { } d) duration = d;
            else if (ItemConfig.ParseTime(it.Out) is { } o) duration = o - inPoint;
            else if (probes[i].Ok) duration = probes[i].Duration - inPoint;
            else duration = TimeSpan.Zero; // inutilisable : sera sauté

            long? fixedStart = it.ParseStartAt(epoch) is { } at ? rate.FramesRound(at - epoch) : null;

            list.Add(new ScheduledItem(
                Index: i,
                Id: it.Id ?? (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                Title: string.IsNullOrWhiteSpace(it.Title) ? Path.GetFileNameWithoutExtension(it.Path) : it.Title,
                Path: config.Resolve(it.Path),
                InFrames: rate.FramesFloor(inPoint),
                DurationFrames: Math.Max(0, rate.FramesFloor(duration)),
                FixedStartFrame: fixedStart,
                GainDb: it.GainDb));
        }
        return list;
    }
}
