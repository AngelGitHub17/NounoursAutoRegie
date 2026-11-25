using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NAR.Server.Data;

namespace NAR.Server.Services;

/// <summary>
/// Analyse les médias en attente, un à la fois et en priorité basse (l'antenne passe avant) :
/// technique (ffprobe), volume EBU R128 → correction de gain, vignette.
/// Relance aussi un parcours des dossiers à intervalle régulier.
/// </summary>
public sealed class IngestWorker(
    IDbContextFactory<NarDbContext> dbFactory,
    MediaLibrary library,
    FfmpegTools ffmpeg,
    Network.SmbSources smb,
    IOptions<NarOptions> options,
    ILogger<IngestWorker> logger) : BackgroundService
{
    private readonly SemaphoreSlim _signal = new(0);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        library.WorkAvailable += () => _signal.Release();
        await ResetInterruptedAsync(stoppingToken);
        var nextScan = DateTimeOffset.UtcNow.AddSeconds(5);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (DateTimeOffset.UtcNow >= nextScan)
                {
                    nextScan = DateTimeOffset.UtcNow.AddMinutes(Math.Max(1, options.Value.RescanMinutes));
                    await library.ScanAllAsync(stoppingToken);
                }

                if (!await AnalyzeNextAsync(stoppingToken))
                    await _signal.WaitAsync(TimeSpan.FromSeconds(30), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "Erreur de l'analyseur de médias");
                await Task.Delay(5000, stoppingToken);
            }
        }
    }

    private async Task ResetInterruptedAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.MediaAssets.Where(a => a.Status == AssetStatus.Analyse)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, AssetStatus.EnAttente), ct);
    }

    private async Task<bool> AnalyzeNextAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var asset = await db.MediaAssets.Where(a => a.Status == AssetStatus.EnAttente).OrderBy(a => a.Id).FirstOrDefaultAsync(ct);
        if (asset is null) return false;

        asset.Status = AssetStatus.Analyse;
        await db.SaveChangesAsync(ct);
        library.NotifyChanged();

        if (!Network.SmbSources.IsRemote(asset.Path) && !File.Exists(asset.Path))
        {
            asset.Status = AssetStatus.Absent;
            asset.Error = "Fichier introuvable";
            await db.SaveChangesAsync(ct);
            library.NotifyChanged();
            return true;
        }

        var started = DateTimeOffset.UtcNow;
        var source = smb.Playable(asset);
        var probe = await ffmpeg.ProbeAsync(source, ct);
        if (!probe.Ok)
        {
            asset.Status = AssetStatus.Erreur;
            asset.Error = probe.Error;
            asset.AnalyzedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            library.NotifyChanged();
            logger.LogWarning("Média illisible « {Title} » : {Error}", asset.Title, probe.Error);
            return true;
        }

        asset.Duration = probe.Duration;
        asset.Width = probe.Width;
        asset.Height = probe.Height;
        asset.FrameRate = probe.FrameRate;
        asset.VideoCodec = probe.VideoCodec;
        asset.AudioCodec = probe.AudioCodec;
        asset.HasAudio = probe.HasAudio;

        asset.LoudnessLufs = probe.HasAudio ? await ffmpeg.MeasureLoudnessAsync(source, ct) : null;
        asset.GainDb = FfmpegTools.GainFor(asset.LoudnessLufs);
        asset.HasThumbnail = await ffmpeg.CreateThumbnailAsync(source, asset.Duration, library.ThumbnailPath(asset.Id), ct);

        asset.Status = AssetStatus.Pret;
        asset.Error = null;
        asset.AnalyzedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        library.NotifyChanged();

        logger.LogInformation("Analysé « {Title} » : {Duration}, {W}x{H}, {Lufs} LUFS → gain {Gain:+0.0;-0.0} dB ({Elapsed:0.0} s)",
            asset.Title, asset.Duration.ToString(@"hh\:mm\:ss"), asset.Width, asset.Height,
            asset.LoudnessLufs?.ToString("0.0") ?? "—", asset.GainDb, (DateTimeOffset.UtcNow - started).TotalSeconds);
        return true;
    }
}
