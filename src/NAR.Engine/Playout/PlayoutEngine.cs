using System.Diagnostics;
using NAR.Core.Config;
using NAR.Core.Media;
using NAR.Core.Scheduling;
using NAR.Core.Status;
using NAR.Engine.Config;
using NAR.Engine.Overlay;

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

        // Carton d'excuse (logo de la chaîne) ; la mire de barres ne sert plus qu'en dernier recours.
        string? slateLogo = config.Overlays.Logo is { } lp ? config.Resolve(lp) + "|" + config.Overlays.LogoVersion : null;
        var slate = ApologySlate.Create(format, config.Overlays.Logo is { } l0 ? config.Resolve(l0) : null, config.Name, _logger);
        var apologyShown = false;
        long frozenFrames = 0;
        var video = new byte[format.VideoFrameBytes];
        // Image habillée : copie de l'image source (qui peut être figée et resservie) + incrustations.
        var dressed = new byte[format.VideoFrameBytes];
        using var overlays = new OverlayCompositor(format, loggerFactory.CreateLogger<OverlayCompositor>());
        overlays.Configure(config.Overlays, config.Resolve);
        var audio = new byte[format.MaxAudioBytesPerFrame];
        Buffer.BlockCopy(slate, 0, video, 0, slate.Length);

        SourceDecoder? current = null;
        ScheduledItem? currentItem = null;
        // Position de départ de la source en cours (pour la relancer au même endroit) et nombre de relances.
        long currentStartFrame = 0, currentOffset = 0;
        var currentRetries = 0;
        long failureSeenFrame = -1;
        long currentShown = 0; // images de la source en cours réellement passées à l'antenne
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

        OverlayContext OverlayContextAt(long f)
        {
            var wall = epoch + rate.TimeFromFrames(f);
            if (apologyShown)
                return new OverlayContext(wall, OnAir: false, Overlay: false, IsProgramme: false, Rating: null, 0, 0, null, null);
            if (currentItem is null)
                return new OverlayContext(wall, OnAir: state == AntennaState.Filler, Overlay: state == AntennaState.Filler,
                    IsProgramme: false, Rating: null, ElapsedSeconds: 10, RemainingSeconds: 10, null, null);

            // Fin effective : la fin naturelle, ou la coupure par un élément à horaire fixe.
            var end = conductor.CurrentEndFrame;
            var nextIndex = conductor.NextIndex;
            if (nextIndex >= 0 && items[nextIndex].FixedStartFrame is { } cut && cut > f && cut < end) end = cut;
            var remaining = Math.Max(0, end - f) / rate.FramesPerSecond;
            var elapsed = Math.Max(0, currentItem.DurationFrames - (conductor.CurrentEndFrame - f)) / rate.FramesPerSecond;

            // Prochain programme (les pubs et jingles intercalés sont sautés) et son heure prévue.
            string? nextTitle = null;
            DateTimeOffset? nextStart = null;
            var t = end;
            for (var (i, n) = (nextIndex, 0); i >= 0 && n < items.Count; n++)
            {
                var it = items[i];
                if (it.FixedStartFrame is { } fs && fs > t) t = fs;
                if (it.DurationFrames > 0 && it.Programme)
                {
                    nextTitle = it.Title;
                    nextStart = epoch + rate.TimeFromFrames(t);
                    break;
                }
                t += it.DurationFrames;
                i = i + 1 < items.Count ? i + 1 : plan.Loop ? 0 : -1;
            }
            return new OverlayContext(wall, OnAir: true, currentItem.Overlay, currentItem.Programme, currentItem.Rating,
                elapsed, remaining, nextTitle, nextStart);
        }

        void ApplyPlan(PlayoutPlan next)
        {
            var fillerChanged = next.FillerPath != plan.FillerPath;
            plan = next;
            items = next.Items;
            conductor.Reload(items, next.Loop);
            overlays.Configure(next.Config.Overlays, next.Config.Resolve);
            var logoKey = next.Config.Overlays.Logo is { } nl ? next.Config.Resolve(nl) + "|" + next.Config.Overlays.LogoVersion : null;
            if (logoKey != slateLogo)
            {
                slateLogo = logoKey;
                slate = ApologySlate.Create(format, next.Config.Overlays.Logo is { } l1 ? next.Config.Resolve(l1) : null, next.Config.Name, _logger);
            }

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

                // --- Source réseau en échec : nouvel essai au même endroit --------------------
                // Jellyfin ou un partage Windows peuvent mettre du temps à répondre ou décrocher un
                // instant : plutôt que de sauter tout un film, on le relance au même endroit (deux fois,
                // après 2 puis 4 s, image figée en attendant).
                var retryPending = false;
                // Une source réseau qui s'arrête bien avant sa fin prévue a été coupée en route (le lecteur
                // y voit une simple fin de fichier) : on la traite comme une panne.
                var cutShort = current is { IsFinished: true, Failure: null, Buffered: 0 } && currentItem is not null
                               && IsRemote(currentItem.Path) && currentOffset + currentShown < currentItem.DurationFrames - fps * 3;
                if (cutShort && failureSeenFrame < 0)
                    _logger.LogWarning("{Tc} « {Title} » s'est arrêté à +{Pos} au lieu de {Dur} : flux interrompu", rate.ToTimecode(frame),
                        currentItem!.Title, rate.ToTimecode(currentOffset + currentShown), rate.ToTimecode(currentItem.DurationFrames));
                if ((current is { Failure: not null } || cutShort) && currentItem is not null && currentRetries < 2 && IsRemote(currentItem.Path))
                {
                    var position = currentOffset + currentShown;
                    if (position < currentItem.DurationFrames - fps * 5)
                    {
                        if (failureSeenFrame < 0) failureSeenFrame = frame;
                        if (frame - failureSeenFrame < fps * 2L * (currentRetries + 1))
                        {
                            retryPending = true;
                        }
                        else
                        {
                            currentRetries++;
                            var why = current!.Failure ?? "flux interrompu";
                            _logger.LogWarning("{Tc} Nouvel essai de « {Title} » ({N}/2) à +{Pos} après : {Why}", rate.ToTimecode(frame),
                                currentItem.Title, currentRetries, rate.ToTimecode(position), why);
                            asRun.Write("retry", frame, rate.ToTimecode(frame), currentItem.Id, currentItem.Title, detail: why);
                            lastIncident = $"{DateTimeOffset.Now:HH:mm:ss} {currentItem.Title} : nouvel essai ({why})";
                            Dispose(current);
                            current = CreateDecoder(currentItem, position);
                            currentStartFrame = frame;
                            currentOffset = position;
                            currentShown = 0;
                            underrun = 0;
                            failureSeenFrame = -1;
                        }
                    }
                }

                // --- Décision du conducteur -------------------------------------------------
                // Si l'élément démarré est inutilisable sur-le-champ (fichier corrompu…), on redemande
                // une décision dans la même image : aucune image n'est perdue.
                for (var attempt = 0; attempt < 4; attempt++)
                {
                    var decision = conductor.Tick(frame, !retryPending && (current is null || current.IsFinished));

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
                            if (preloads.Remove(Key(item), out var ready) && decision.OffsetFrames == 0 && ready.Failure is null)
                            {
                                current = ready;
                            }
                            else
                            {
                                // Préchargement absent, décalé, ou déjà en échec (serveur qui a refusé) : départ neuf.
                                if (ready?.Failure is { } why)
                                    _logger.LogWarning("Préchargement de « {Title} » en échec ({Why}) : nouveau départ", item.Title, why);
                                Dispose(ready);
                                current = CreateDecoder(item, decision.OffsetFrames);
                            }
                            currentItem = item;
                            currentStartFrame = frame;
                            currentOffset = decision.OffsetFrames;
                            currentRetries = 0;
                            currentShown = 0;
                            failureSeenFrame = -1;
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
                    if (ReferenceEquals(source, current)) currentShown++;
                    state = current is not null ? AntennaState.Playing : AntennaState.Filler;
                    underrun = 0;
                    frozenFrames = 0;
                    apologyShown = false;
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
                        // Patience selon la source : une source réseau qui démarre (surtout prise en cours,
                        // donc avec un saut dans le fichier) peut mettre plusieurs secondes à livrer sa première image.
                        var path = ReferenceEquals(source, current) ? currentItem?.Path : plan.FillerPath;
                        var remote = path is not null && IsRemote(path);
                        var starting = source.Delivered == 0;
                        var limit = starting ? (remote ? 20 : 6) : (remote ? 8 : 2);
                        if (underrun > fps * limit)
                        {
                            source.Fail(starting ? $"aucune image {limit} s après le démarrage" : $"aucune image reçue depuis {limit} s");
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
                        if (state != AntennaState.Slate || !apologyShown) Buffer.BlockCopy(slate, 0, video, 0, slate.Length);
                        state = AntennaState.Slate;
                        apologyShown = true;
                    }
                    // source en cours terminée : le conducteur enchaîne à l'image suivante (image figée d'ici là).

                    // Image figée depuis 10 s (programme introuvable, réseau en panne, nouveaux essais) :
                    // le carton d'excuse remplace l'image arrêtée jusqu'au retour du programme.
                    if (++frozenFrames >= fps * 10 && !apologyShown)
                    {
                        Buffer.BlockCopy(slate, 0, video, 0, slate.Length);
                        apologyShown = true;
                        lastIncident = $"{DateTimeOffset.Now:HH:mm:ss} image figée depuis 10 s : carton d'excuse";
                        _logger.LogWarning("{Tc} Image figée depuis 10 s : carton « Désolé, votre programme ne peut pas être diffusé »", rate.ToTimecode(frame));
                    }
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
                {
                    var context = OverlayContextAt(frame);
                    var output = video;
                    if (overlays.IsActive(context))
                    {
                        Buffer.BlockCopy(video, 0, dressed, 0, video.Length);
                        overlays.Apply(dressed, context);
                        output = dressed;
                    }
                    await encoder.SendAsync(output, audio.AsMemory(0, audioBytes), ct);
                }

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
    private static bool IsRemote(string path) =>
        path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

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
                var path = config.Resolve(itemsConfig[i].Path);
                probes[i] = ChannelConfig.IsUrl(path) && itemsConfig[i].Media is { DurationSeconds: > 0 } known
                    ? new ProbeResult(true, TimeSpan.FromSeconds(known.DurationSeconds), known.HasVideo, known.HasAudio, null)
                    : await ProbeCachedAsync(path, token);
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
                GainDb: it.GainDb,
                Overlay: it.Overlay,
                Programme: it.Programme,
                Rating: it.Rating));
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
