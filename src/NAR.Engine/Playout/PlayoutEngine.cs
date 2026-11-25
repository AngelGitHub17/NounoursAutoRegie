using System.Diagnostics;
using NAR.Core.Config;
using NAR.Core.Media;
using NAR.Core.Scheduling;
using NAR.Core.Status;
using NAR.Engine.Config;

namespace NAR.Engine.Playout;

/// <summary>Conducteur prêt à l'antenne : éléments résolus en images + analyse de chaque média.</summary>
internal sealed record PlayoutPlan(
    long Revision,
    IReadOnlyList<ScheduledItem> Items,
    IReadOnlyDictionary<string, ProbeResult> Probes,
    bool Loop,
    string? FillerPath,
    ProbeResult? FillerProbe,
    ChannelConfig Config);

/// <summary>
/// Le moteur d'une chaîne. Une boucle cadencée par une horloge maîtresse produit exactement
/// une image (et son audio) par période, quoi qu'il arrive : source en cours, bouche-trou,
/// image figée en cas de retard, ou mire. Le conducteur décide, le moteur exécute.
/// Le conducteur peut être remplacé à chaud (régie ou fichier modifié) sans couper l'antenne.
/// </summary>
public sealed class PlayoutEngine(
    ChannelConfig initialConfig,
    IChannelConfigSource configSource,
    MediaProbe probe,
    EngineStatus status,
    IHostApplicationLifetime lifetime,
    ILoggerFactory loggerFactory) : BackgroundService
{
    /// <summary>Code de sortie quand un réglage de structure change : le superviseur relance aussitôt.</summary>
    public const int RestartExitCode = 3;

    private readonly ILogger _logger = loggerFactory.CreateLogger<PlayoutEngine>();
    private readonly Dictionary<string, (long Size, DateTime Write, ProbeResult Result)> _probeCache = new();
    private PlayoutPlan? _pendingPlan;

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
            Environment.ExitCode = 1;
            lifetime.StopApplication();
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var config = initialConfig;
        var format = new ChannelFormat(config.Format.Width, config.Format.Height, FrameRate.Parse(config.Format.FrameRate));
        var rate = format.Rate;
        var fps = rate.NominalFps;
        var asRun = new AsRunLog(config.Resolve(config.AsRunDirectory), config.Name, _logger);
        var preloadFrames = rate.FramesFloor(TimeSpan.FromSeconds(config.PreloadSeconds));

        _logger.LogInformation("Chaîne {Name} : format {Format}, configuration {Source}", config.Name, format, configSource.Description);

        // 1. Époque du moteur : l'image 0 sera diffusée à cet instant précis. On se laisse 2 s pour
        //    lancer l'encodeur et le pré-roll, afin que l'horloge des images colle à l'heure murale.
        var epoch = DateTimeOffset.Now.AddSeconds(2);
        var plan = await BuildPlanAsync(config, rate, epoch, ct);
        var items = plan.Items;
        var conductor = new Conductor(items, plan.Loop, preloadFrames);
        _logger.LogInformation("Conducteur révision {Rev} : {Count} élément(s)", plan.Revision, items.Count);

        // Surveillance des modifications (régie ou fichier) en tâche de fond.
        using var watchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = Task.Run(() => WatchConfigAsync(config, rate, epoch, watchCts.Token));

        // 2. Sortie continue.
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
        var preloads = new Dictionary<string, SourceDecoder>();
        var state = AntennaState.Starting;
        var fallbackAnnounced = false;
        long underrun = 0, underrunTotal = 0, sourceFailures = 0;
        string? lastIncident = null;
        var lastLateWarning = 0L;

        var clock = Stopwatch.StartNew();
        long frame = 0;

        static string Key(ScheduledItem item) => $"{item.Id}|{item.Path}|{item.InFrames}|{item.DurationFrames}";

        SourceDecoder CreateDecoder(ScheduledItem item, long offset)
        {
            var probeResult = plan.Probes.TryGetValue(item.Id, out var p) ? p : ProbeResult.Failed("Média non analysé");
            var decoder = new SourceDecoder(item.Title, item.Path, item.InFrames + offset,
                item.DurationFrames - offset, loop: false, item.GainDb, probeResult, format,
                config.FfmpegPath, loggerFactory.CreateLogger<SourceDecoder>(), bufferFrames: fps);
            decoder.Start();
            return decoder;
        }

        void Dispose(SourceDecoder? decoder)
        {
            if (decoder is not null) _ = Task.Run(() => decoder.DisposeAsync().AsTask());
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
            Dispose(current);
            current = null;
            currentItem = null;
        }

        void ApplyPlan(PlayoutPlan next)
        {
            var fillerChanged = next.FillerPath != plan.FillerPath;
            plan = next;
            items = next.Items;
            conductor.Reload(items, next.Loop);

            // Les préchargements devenus inutiles (élément retiré ou modifié) sont libérés.
            var keep = items.Select(Key).ToHashSet();
            foreach (var key in preloads.Keys.Where(k => !keep.Contains(k)).ToList())
            {
                Dispose(preloads[key]);
                preloads.Remove(key);
            }
            if (fillerChanged)
            {
                Dispose(filler);
                filler = null;
                fillerRetryAt = 0;
            }
            asRun.Write("reload", frame, rate.ToTimecode(frame), detail: $"révision {next.Revision}, {items.Count} élément(s)");
            _logger.LogInformation("{Tc} Conducteur rechargé à chaud : révision {Rev}, {Count} élément(s)",
                rate.ToTimecode(frame), next.Revision, items.Count);
        }

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // --- Modification du conducteur ----------------------------------------------
                if (Interlocked.Exchange(ref _pendingPlan, null) is { } newPlan)
                    ApplyPlan(newPlan);

                // --- Décision du conducteur -------------------------------------------------
                // Si l'élément démarré est inutilisable sur-le-champ (fichier corrompu…), on redemande
                // une décision dans la même image : aucune image n'est perdue.
                for (var attempt = 0; attempt < 4; attempt++)
                {
                    var decision = conductor.Tick(frame, current is null || current.IsFinished);

                    if (decision.PreloadIndex >= 0 && !preloads.ContainsKey(Key(items[decision.PreloadIndex])))
                    {
                        var item = items[decision.PreloadIndex];
                        _logger.LogInformation("Préchargement de « {Title} »", item.Title);
                        preloads[Key(item)] = CreateDecoder(item, 0);
                    }

                    switch (decision.Action)
                    {
                        case ConductorAction.Start:
                        {
                            EndCurrent(current?.IsFinished == false ? "cut" : "end");
                            var item = items[decision.ItemIndex];
                            if (preloads.Remove(Key(item), out var ready) && decision.OffsetFrames == 0)
                            {
                                current = ready;
                            }
                            else
                            {
                                Dispose(ready);
                                current = CreateDecoder(item, decision.OffsetFrames);
                            }
                            currentItem = item;
                            underrun = 0;
                            fallbackAnnounced = false;
                            asRun.Write("start", frame, rate.ToTimecode(frame), item.Id, item.Title,
                                detail: decision.OffsetFrames > 0 ? $"pris en cours (+{rate.ToTimecode(decision.OffsetFrames)})" : null);
                            _logger.LogInformation("{Tc} ▶ « {Title} »{Offset}", rate.ToTimecode(frame), item.Title,
                                decision.OffsetFrames > 0 ? $" (pris à +{rate.ToTimecode(decision.OffsetFrames)})" : "");

                            // Les préchargements qui ne servent plus sont libérés.
                            var nextKey = conductor.NextIndex >= 0 ? Key(items[conductor.NextIndex]) : null;
                            foreach (var key in preloads.Keys.Where(k => k != nextKey).ToList())
                            {
                                Dispose(preloads[key]);
                                preloads.Remove(key);
                            }
                            break;
                        }
                        case ConductorAction.Fallback:
                            EndCurrent("end");
                            if (!fallbackAnnounced)
                            {
                                fallbackAnnounced = true;
                                asRun.Write("fallback", frame, rate.ToTimecode(frame));
                                _logger.LogWarning("{Tc} Rien de prévu : passage en secours", rate.ToTimecode(frame));
                            }
                            break;
                    }
                    if (decision.Action != ConductorAction.Start || current is null || !current.IsFinished) break;
                }

                // Bouche-trou à la demande quand on est en secours.
                if (current is null && filler is null && plan.FillerProbe is { Ok: true } && clock.ElapsedMilliseconds >= fillerRetryAt)
                {
                    filler = new SourceDecoder("bouche-trou", plan.FillerPath!, 0, long.MaxValue, loop: true, 0,
                        plan.FillerProbe, format, config.FfmpegPath, loggerFactory.CreateLogger<SourceDecoder>(), bufferFrames: fps);
                    filler.Start();
                }
                else if (current is not null && filler is not null)
                {
                    Dispose(filler);
                    filler = null;
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
                        Dispose(filler);
                        filler = null;
                        fillerRetryAt = clock.ElapsedMilliseconds + 30_000;
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
                        currentItem is null ? null : currentItem.DurationFrames / rate.FramesPerSecond,
                        next >= 0 ? items[next].Title : null,
                        next >= 0 ? items[next].Id : null,
                        plan.Revision,
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
            watchCts.Cancel();
            EndCurrent("stop");
            foreach (var p in preloads.Values) await p.DisposeAsync();
            if (filler is not null) await filler.DisposeAsync();
            await encoder.DisposeAsync();
            asRun.Write("shutdown", frame, rate.ToTimecode(frame));
            _logger.LogInformation("Moteur arrêté à {Tc}", rate.ToTimecode(frame));
        }
    }

    /// <summary>Relit la configuration toutes les 2 s et prépare le nouveau conducteur hors de la boucle d'antenne.</summary>
    private async Task WatchConfigAsync(ChannelConfig running, FrameRate rate, DateTimeOffset epoch, CancellationToken ct)
    {
        var fingerprint = running.StructuralFingerprint();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(2000, ct);
                var updated = await configSource.PollAsync(ct);
                if (updated is null) continue;

                if (updated.StructuralFingerprint() != fingerprint)
                {
                    _logger.LogWarning("Format, encodeur ou sortie modifiés : redémarrage du moteur pour les appliquer");
                    Environment.ExitCode = RestartExitCode;
                    lifetime.StopApplication();
                    return;
                }
                Volatile.Write(ref _pendingPlan, await BuildPlanAsync(updated, rate, epoch, ct));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Rechargement du conducteur impossible, on garde le précédent");
            }
        }
    }

    private async Task<PlayoutPlan> BuildPlanAsync(ChannelConfig config, FrameRate rate, DateTimeOffset epoch, CancellationToken ct)
    {
        var itemsConfig = config.Playlist.Items;
        var probes = new ProbeResult[itemsConfig.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, probes.Length),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            async (i, token) =>
            {
                probes[i] = await ProbeCachedAsync(config.Resolve(itemsConfig[i].Path), token);
                if (!probes[i].Ok)
                    _logger.LogWarning("Élément « {Title} » inutilisable : {Error}", itemsConfig[i].Title, probes[i].Error);
            });

        ProbeResult? fillerProbe = null;
        string? fillerPath = null;
        if (!string.IsNullOrWhiteSpace(config.Filler))
        {
            fillerPath = config.Resolve(config.Filler!);
            fillerProbe = await ProbeCachedAsync(fillerPath, ct);
            if (!fillerProbe.Ok) _logger.LogWarning("Bouche-trou inutilisable ({Error}) : la mire prendra le relais", fillerProbe.Error);
        }

        var list = new List<ScheduledItem>();
        var byId = new Dictionary<string, ProbeResult>();
        for (var i = 0; i < itemsConfig.Count; i++)
        {
            var it = itemsConfig[i];
            var inPoint = ItemConfig.ParseTime(it.In) ?? TimeSpan.Zero;
            TimeSpan duration;
            if (ItemConfig.ParseTime(it.Duration) is { } d) duration = d;
            else if (ItemConfig.ParseTime(it.Out) is { } o) duration = o - inPoint;
            else if (probes[i].Ok) duration = probes[i].Duration - inPoint;
            else duration = TimeSpan.Zero; // inutilisable : sera sauté
            if (!probes[i].Ok) duration = TimeSpan.Zero;

            long? fixedStart = it.ParseStartAt(epoch) is { } at ? rate.FramesRound(at - epoch) : null;
            var id = string.IsNullOrWhiteSpace(it.Id) ? $"#{i + 1}" : it.Id!;
            byId[id] = probes[i];

            list.Add(new ScheduledItem(
                Index: i,
                Id: id,
                Title: string.IsNullOrWhiteSpace(it.Title) ? Path.GetFileNameWithoutExtension(it.Path) : it.Title,
                Path: config.Resolve(it.Path),
                InFrames: rate.FramesFloor(inPoint),
                DurationFrames: Math.Max(0, rate.FramesFloor(duration)),
                FixedStartFrame: fixedStart,
                GainDb: it.GainDb));
        }
        return new PlayoutPlan(config.Revision, list, byId, config.Playlist.Loop, fillerPath, fillerProbe, config);
    }

    private async Task<ProbeResult> ProbeCachedAsync(string path, CancellationToken ct)
    {
        if (ChannelConfig.IsUrl(path))
        {
            // Média distant : l'adresse change quand le fichier change (paramètre de version), on garde
            // donc les réussites ; un échec (PC éteint…) est retenté au prochain rechargement.
            lock (_probeCache)
            {
                if (_probeCache.TryGetValue(path, out var hit) && hit.Result.Ok) return hit.Result;
            }
            var remote = await probe.ProbeAsync(path, ct);
            lock (_probeCache) _probeCache[path] = (0, DateTime.MinValue, remote);
            return remote;
        }

        var info = new FileInfo(path);
        if (!info.Exists) return ProbeResult.Failed($"Fichier introuvable : {path}");
        lock (_probeCache)
        {
            if (_probeCache.TryGetValue(path, out var cached) && cached.Size == info.Length && cached.Write == info.LastWriteTimeUtc)
                return cached.Result;
        }
        var result = await probe.ProbeAsync(path, ct);
        lock (_probeCache) _probeCache[path] = (info.Length, info.LastWriteTimeUtc, result);
        return result;
    }

    private static bool ReadFrame(SourceDecoder source, byte[] video, byte[] audio, int audioBytes) =>
        source.TryReadFrame(video, audio.AsSpan(0, audioBytes));
}
