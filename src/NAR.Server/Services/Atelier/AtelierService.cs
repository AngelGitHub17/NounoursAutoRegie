using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NAR.Core.Media;
using NAR.Server.Data;
using NAR.Server.Services.Network;

namespace NAR.Server.Services.Atelier;

/// <summary>
/// Atelier d'habillage : sons envoyés (et leur rythme), logos de chaîne, et fabrication en tâche
/// de fond des jingles, identités, génériques et bandes-annonces « Rétro 95 ». Chaque création
/// terminée entre dans la médiathèque (dossier « Atelier ») et peut aller dans un conducteur.
/// </summary>
public sealed class AtelierService(
    IDbContextFactory<NarDbContext> dbFactory,
    AudioAnalyzer analyzer,
    IOptions<NarOptions> options,
    ILogger<AtelierService> logger)
{
    public static readonly string[] AudioExtensions = [".mp3", ".wav", ".ogg", ".oga", ".flac", ".m4a", ".aac", ".opus", ".wma"];
    /// <summary>« fin nette » ou « fin qui résonne (2,1 s) », d'après l'analyse enregistrée.</summary>
    public static string EndingLabel(AtelierSound sound)
    {
        try
        {
            var rhythm = JsonSerializer.Deserialize<RhythmAnalysis>(sound.AnalysisJson, Json);
            return rhythm?.Ending(rhythm.Duration).Label ?? "";
        }
        catch (JsonException) { return ""; }
    }

    public static readonly JsonSerializerOptions Json = new() { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };

    private readonly SemaphoreSlim _signal = new(0);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, double> _live = new();

    public event Action? Changed;

    /// <summary>Avancement en direct d'une fabrication (0 à 1), sans écrire en base à chaque image.</summary>
    public double? LiveProgress(int id) => _live.TryGetValue(id, out var p) ? p : null;
    internal void SetProgress(int id, double? p)
    {
        if (p is { } v) _live[id] = v; else _live.TryRemove(id, out _);
    }

    public string Root => Path.Combine(options.Value.ResolvedDataDirectory, "atelier");
    public string SoundsDirectory => Path.Combine(Root, "sons");
    public string CreationsDirectory => Path.Combine(Root, "creations");
    public string LogoPath(int channelId) => Path.Combine(options.Value.ChannelDirectory(channelId), "logo.png");

    public void NotifyChanged() => Changed?.Invoke();
    internal Task WaitForWorkAsync(TimeSpan timeout, CancellationToken ct) => _signal.WaitAsync(timeout, ct);

    public async Task<AtelierSound> AddSoundAsync(Stream content, string originalName, CancellationToken ct)
    {
        var ext = Path.GetExtension(originalName).ToLowerInvariant();
        if (!AudioExtensions.Contains(ext)) throw new InvalidDataException("Format de son non reconnu (mp3, wav, ogg, flac, m4a…).");
        Directory.CreateDirectory(SoundsDirectory);
        var stored = $"{Guid.NewGuid():N}{ext}";
        var path = Path.Combine(SoundsDirectory, stored);
        await using (var file = File.Create(path)) await content.CopyToAsync(file, ct);

        RhythmAnalysis rhythm;
        try
        {
            rhythm = await analyzer.AnalyzeAsync(path, ct);
        }
        catch
        {
            File.Delete(path);
            throw;
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var sound = new AtelierSound
        {
            Name = Path.GetFileNameWithoutExtension(originalName),
            FileName = stored,
            Duration = Math.Round(rhythm.Duration, 2),
            Bpm = rhythm.Bpm,
            BeatCount = rhythm.Beats.Length,
            AnalysisJson = JsonSerializer.Serialize(rhythm, Json),
        };
        db.AtelierSounds.Add(sound);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Son « {Name} » : {Duration:0.0} s, {Bpm} BPM, {Beats} temps", sound.Name, sound.Duration, sound.Bpm, sound.BeatCount);
        Changed?.Invoke();
        return sound;
    }

    public async Task DeleteSoundAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var sound = await db.AtelierSounds.FindAsync(id);
        if (sound is null) return;
        db.AtelierSounds.Remove(sound);
        await db.SaveChangesAsync();
        try { File.Delete(Path.Combine(SoundsDirectory, sound.FileName)); } catch { }
        Changed?.Invoke();
    }

    public async Task SaveLogoAsync(int channelId, Stream content, CancellationToken ct)
    {
        // Conversion en PNG (garde la transparence) et taille raisonnable.
        using var memory = new MemoryStream();
        await content.CopyToAsync(memory, ct);
        memory.Position = 0;
        using var bitmap = SkiaSharp.SKBitmap.Decode(memory) ?? throw new InvalidDataException("Image illisible (PNG, JPEG, WebP…).");
        var scale = Math.Min(1.0, 1200.0 / Math.Max(bitmap.Width, bitmap.Height));
        using var resized = scale < 1
            ? bitmap.Resize(new SkiaSharp.SKImageInfo((int)(bitmap.Width * scale), (int)(bitmap.Height * scale)), SkiaSharp.SKFilterQuality.High)
            : bitmap.Copy();
        using var image = SkiaSharp.SKImage.FromBitmap(resized);
        using var png = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        var path = LogoPath(channelId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using (var file = File.Create(path)) png.SaveTo(file);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var channel = await db.Channels.FindAsync([channelId], ct);
        if (channel is not null)
        {
            channel.HasLogo = true;
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task<AtelierCreation> QueueAsync(AtelierCreation creation)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        creation.Status = CreationStatus.EnAttente;
        creation.Progress = 0;
        if (creation.Seed == 0) creation.Seed = Random.Shared.Next(1, int.MaxValue);
        db.AtelierCreations.Add(creation);
        await db.SaveChangesAsync();
        _signal.Release();
        Changed?.Invoke();
        return creation;
    }

    /// <summary>Refaire avec un autre tirage de mosaïque (mêmes réglages).</summary>
    public async Task RedoAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var c = await db.AtelierCreations.FindAsync(id);
        if (c is null) return;
        c.Seed = Random.Shared.Next(1, int.MaxValue);
        c.Status = CreationStatus.EnAttente;
        c.Progress = 0;
        c.Error = null;
        await db.SaveChangesAsync();
        _signal.Release();
        Changed?.Invoke();
    }

    public async Task DeleteCreationAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var c = await db.AtelierCreations.FindAsync(id);
        if (c is null) return;
        db.AtelierCreations.Remove(c);
        await db.SaveChangesAsync();
        Changed?.Invoke();
    }

    public static AssetKind AssetKindOf(AtelierKind kind) => kind switch
    {
        AtelierKind.BandeAnnonce => AssetKind.BandeAnnonce,
        AtelierKind.Generique => AssetKind.Habillage,
        _ => AssetKind.Jingle,
    };

    public static string KindLabel(AtelierKind kind) => kind switch
    {
        AtelierKind.Jingle => "Jingle avant programme",
        AtelierKind.Ident => "Identité de chaîne",
        AtelierKind.Generique => "Générique",
        AtelierKind.BandeAnnonce => "Bande-annonce",
        _ => kind.ToString(),
    };

    public static string Slug(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-');
        }
        var slug = string.Join('-', sb.ToString().Split('-', StringSplitOptions.RemoveEmptyEntries));
        return slug.Length == 0 ? "habillage" : slug.Length > 50 ? slug[..50] : slug;
    }
}

/// <summary>Fabrique les créations en attente, une à la fois, en priorité basse.</summary>
public sealed class AtelierWorker(
    IDbContextFactory<NarDbContext> dbFactory,
    AtelierService atelier,
    Retro95Renderer renderer,
    MediaLibrary library,
    SmbSources sources,
    FfmpegTools ffmpeg,
    ILogger<AtelierWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        // Une fabrication interrompue (redémarrage) repart de zéro.
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);
            await db.AtelierCreations.Where(c => c.Status == CreationStatus.EnCours)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, CreationStatus.EnAttente), stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Remise en file des créations interrompues impossible");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await RenderNextAsync(stoppingToken))
                    await atelier.WaitForWorkAsync(TimeSpan.FromSeconds(30), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "Erreur de l'atelier d'habillage");
                await Task.Delay(3000, stoppingToken);
            }
        }
    }

    private async Task<bool> RenderNextAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var creation = await db.AtelierCreations.Include(c => c.Sound)
            .Where(c => c.Status == CreationStatus.EnAttente).OrderBy(c => c.Id).FirstOrDefaultAsync(ct);
        if (creation is null) return false;

        creation.Status = CreationStatus.EnCours;
        creation.Error = null;
        await db.SaveChangesAsync(ct);
        atelier.NotifyChanged();

        try
        {
            var job = await BuildJobAsync(db, creation, ct);
            var lastSaved = 0.0;
            var progress = new Progress<double>(p =>
            {
                atelier.SetProgress(creation.Id, p);
                if (p - lastSaved >= 0.05)
                {
                    lastSaved = p;
                    atelier.NotifyChanged();
                }
            });
            var started = DateTimeOffset.UtcNow;
            await renderer.RenderAsync(job, progress, ct);

            // Entrée dans la médiathèque (dossier « Atelier »), avec le bon type et le titre.
            var folder = await db.MediaFolders.FirstOrDefaultAsync(f => f.Path == atelier.CreationsDirectory, ct);
            if (folder is null)
            {
                folder = new MediaFolder { Path = atelier.CreationsDirectory, DefaultKind = AssetKind.Habillage };
                db.MediaFolders.Add(folder);
                await db.SaveChangesAsync(ct);
            }
            await library.ScanFolderAsync(folder.Id, ct);
            var asset = await db.MediaAssets.FirstOrDefaultAsync(a => a.Path == job.OutputPath, ct);
            if (asset is not null)
            {
                await db.Entry(asset).ReloadAsync(ct);
                asset.Kind = AtelierService.AssetKindOf((AtelierKind)creation.Kind);
                asset.Title = $"{AtelierService.KindLabel((AtelierKind)creation.Kind)} — {creation.Title}";
            }

            creation.Status = CreationStatus.Pret;
            creation.Progress = 1;
            creation.AssetId = asset?.Id;
            creation.FinishedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Atelier : « {Title} » fabriqué en {Seconds:0.0} s", creation.Title, (DateTimeOffset.UtcNow - started).TotalSeconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            creation.Status = CreationStatus.Erreur;
            creation.Error = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
            await db.SaveChangesAsync(CancellationToken.None);
            logger.LogWarning("Atelier : échec de « {Title} » : {Error}", creation.Title, ex.Message);
        }
        atelier.SetProgress(creation.Id, null);
        atelier.NotifyChanged();
        return true;
    }

    private async Task<AtelierJob> BuildJobAsync(NarDbContext db, AtelierCreation c, CancellationToken ct)
    {
        var kind = (AtelierKind)c.Kind;
        var channel = c.ChannelId is { } chId ? await db.Channels.AsNoTracking().FirstOrDefaultAsync(x => x.Id == chId, ct) : null;
        var width = channel?.Width ?? 1920;
        var height = channel?.Height ?? 1080;
        var rate = FrameRate.Parse(channel?.FrameRate ?? "25");
        var logo = channel is { HasLogo: true } ? atelier.LogoPath(channel.Id) : null;

        string? soundPath = null;
        RhythmAnalysis rhythm;
        double defaultDuration = kind == AtelierKind.BandeAnnonce ? 30 : 10;
        if (c.Sound is { } sound)
        {
            soundPath = Path.Combine(atelier.SoundsDirectory, sound.FileName);
            if (!File.Exists(soundPath)) throw new FileNotFoundException("Le son de cette création a été supprimé.");
            rhythm = JsonSerializer.Deserialize<RhythmAnalysis>(sound.AnalysisJson, AtelierService.Json)
                     ?? throw new InvalidDataException("Analyse du son illisible : renvoyez le son.");
            defaultDuration = sound.Duration;
        }
        else
        {
            rhythm = RhythmAnalysis.Grid(Math.Max(c.DurationSeconds ?? defaultDuration, 2));
        }
        var maxDuration = kind == AtelierKind.BandeAnnonce ? 180 : 90;
        var duration = Math.Clamp(c.DurationSeconds is > 0 and var d ? Math.Min(d, soundPath is null ? d : rhythm.Duration) : defaultDuration, 2, maxDuration);

        string? source = null;
        double[] starts = [];
        if (kind == AtelierKind.BandeAnnonce)
        {
            var asset = c.SourceAssetId is { } aid ? await db.MediaAssets.AsNoTracking().FirstOrDefaultAsync(a => a.Id == aid, ct) : null;
            if (asset is not { Status: AssetStatus.Pret }) throw new InvalidOperationException("Choisissez un programme prêt (analysé) pour la bande-annonce.");
            source = sources.Playable(asset);
            var check = await ffmpeg.ProbeAsync(source, ct);
            if (!check.Ok) throw new InvalidOperationException($"Programme illisible pour la bande-annonce ({check.Error}). Sa source (partage, Jellyfin) est-elle joignable ?");
            var total = asset.Duration.TotalSeconds;
            starts = (c.ExtractStarts ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => Components.Shared.Fmt.TryParseTime(s, out var ts) ? ts.TotalSeconds : -1)
                .Where(s => s >= 0 && s < total - 1)
                .ToArray();
            if (starts.Length == 0)
            {
                // Extraits répartis entre 10 % et 85 % du programme (on évite génériques et fin).
                const int count = 10;
                starts = Enumerable.Range(0, count).Select(i => total * (0.10 + 0.75 * i / (count - 1))).ToArray();
            }
        }

        var output = Path.Combine(atelier.CreationsDirectory, $"{AtelierService.Slug(AtelierService.KindLabel(kind))}-{AtelierService.Slug(c.Title)}-{c.Id}.mp4");
        return new AtelierJob(kind, c.Title, c.Subtitle, c.Label, Palettes.Get(c.PaletteKey), soundPath, rhythm, logo, c.LogoOnBlack,
            width, height, rate, duration, source, starts, c.Seed, output);
    }
}
