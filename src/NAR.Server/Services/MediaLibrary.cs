using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NAR.Server.Data;

namespace NAR.Server.Services;

/// <summary>
/// Médiathèque : parcours des dossiers surveillés, création des fiches, et file d'analyse
/// (technique, volume EBU R128, vignette) traitée en tâche de fond par <see cref="IngestWorker"/>.
/// </summary>
public sealed partial class MediaLibrary(
    IDbContextFactory<NarDbContext> dbFactory,
    IOptions<NarOptions> options,
    ILogger<MediaLibrary> logger)
{
    public static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".avi", ".ts", ".m2ts", ".mts", ".mxf", ".webm", ".mpg", ".mpeg", ".m4v", ".wmv", ".flv",
    };

    private readonly SemaphoreSlim _scanLock = new(1, 1);

    /// <summary>Réveille l'analyseur dès qu'il y a du travail.</summary>
    public event Action? WorkAvailable;

    /// <summary>Notifie la régie qu'une fiche a changé (analyse terminée…).</summary>
    public event Action? Changed;

    public void NotifyChanged() => Changed?.Invoke();

    public async Task<(int Added, int Updated, int Missing)> ScanFolderAsync(int folderId, CancellationToken ct = default)
    {
        await _scanLock.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var folder = await db.MediaFolders.FindAsync([folderId], ct) ?? throw new InvalidOperationException("Dossier inconnu");
            if (!Directory.Exists(folder.Path))
            {
                logger.LogWarning("Dossier de médiathèque introuvable : {Path}", folder.Path);
                return (0, 0, 0);
            }

            var files = Directory.EnumerateFiles(folder.Path, "*",
                    new EnumerationOptions { RecurseSubdirectories = folder.Recursive, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System })
                .Where(f => VideoExtensions.Contains(Path.GetExtension(f)))
                .Select(f => new FileInfo(f))
                .ToList();

            var known = await db.MediaAssets.Where(a => a.FolderId == folder.Id).ToDictionaryAsync(a => a.Path, ct);
            int added = 0, updated = 0, missing = 0;
            var seen = new HashSet<string>();

            foreach (var file in files)
            {
                seen.Add(file.FullName);
                if (known.TryGetValue(file.FullName, out var asset))
                {
                    if (asset.FileSize != file.Length || asset.FileModifiedUtc != file.LastWriteTimeUtc || asset.Status == AssetStatus.Absent)
                    {
                        asset.FileSize = file.Length;
                        asset.FileModifiedUtc = file.LastWriteTimeUtc;
                        asset.Status = AssetStatus.EnAttente;
                        updated++;
                    }
                    continue;
                }

                // Un fichier déjà connu via un autre dossier n'est pas dupliqué.
                if (await db.MediaAssets.AnyAsync(a => a.Path == file.FullName, ct)) continue;

                db.MediaAssets.Add(new MediaAsset
                {
                    FolderId = folder.Id,
                    Path = file.FullName,
                    FileName = file.Name,
                    Title = TitleFromFileName(file.Name),
                    Kind = GuessKind(file.Name, folder.DefaultKind),
                    FileSize = file.Length,
                    FileModifiedUtc = file.LastWriteTimeUtc,
                });
                added++;
            }

            foreach (var asset in known.Values.Where(a => !seen.Contains(a.Path) && a.Status != AssetStatus.Absent))
            {
                asset.Status = AssetStatus.Absent;
                asset.Error = "Fichier introuvable lors du dernier parcours";
                missing++;
            }

            folder.LastScanAt = DateTimeOffset.UtcNow;
            folder.LastScanCount = files.Count;
            await db.SaveChangesAsync(ct);

            logger.LogInformation("Médiathèque {Path} : {Added} ajouté(s), {Updated} modifié(s), {Missing} absent(s)",
                folder.Path, added, updated, missing);
            if (added + updated > 0) WorkAvailable?.Invoke();
            Changed?.Invoke();
            return (added, updated, missing);
        }
        finally
        {
            _scanLock.Release();
        }
    }

    public async Task ScanAllAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var ids = await db.MediaFolders.Where(f => f.Enabled).Select(f => f.Id).ToListAsync(ct);
        foreach (var id in ids)
        {
            try { await ScanFolderAsync(id, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "Parcours du dossier {Id} impossible", id); }
        }
    }

    public async Task ReanalyzeAsync(int assetId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var asset = await db.MediaAssets.FindAsync(assetId);
        if (asset is null) return;
        asset.Status = AssetStatus.EnAttente;
        asset.Error = null;
        await db.SaveChangesAsync();
        WorkAvailable?.Invoke();
        Changed?.Invoke();
    }

    public string ThumbnailPath(int assetId) => Path.Combine(options.Value.ThumbnailDirectory, $"{assetId}.jpg");

    public static string TitleFromFileName(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName).Replace('_', ' ').Replace('.', ' ');
        name = SpacesRegex().Replace(name, " ").Trim();
        return name.Length == 0 ? fileName : char.ToUpper(name[0]) + name[1..];
    }

    /// <summary>Devine le type d'un média d'après son nom (le dossier donne la valeur par défaut).</summary>
    public static AssetKind GuessKind(string fileName, AssetKind fallback)
    {
        var n = fileName.ToLowerInvariant();
        if (n.Contains("bouche-trou") || n.Contains("bouche_trou") || n.Contains("filler")) return AssetKind.BoucheTrou;
        if (n.Contains("jingle") || n.Contains("virgule") || n.Contains("ident")) return AssetKind.Jingle;
        if (n.Contains("bande-annonce") || n.Contains("bande_annonce") || n.Contains("trailer") || n.Contains("promo")) return AssetKind.BandeAnnonce;
        if (n.Contains("spot") || n.StartsWith("pub") || n.Contains("_pub") || n.Contains("-pub")) return AssetKind.Spot;
        if (n.Contains("habillage") || n.Contains("generique") || n.Contains("générique")) return AssetKind.Habillage;
        if (EpisodeRegex().IsMatch(n)) return AssetKind.Episode;
        return fallback;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpacesRegex();

    [GeneratedRegex(@"s\d{1,2}\s?e\d{1,3}")]
    private static partial Regex EpisodeRegex();
}
