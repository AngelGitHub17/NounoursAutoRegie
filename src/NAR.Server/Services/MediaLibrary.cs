using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NAR.Server.Data;
using NAR.Server.Services.Network;

namespace NAR.Server.Services;

/// <summary>
/// Médiathèque : parcours des dossiers surveillés, création des fiches, et file d'analyse
/// (technique, volume EBU R128, vignette) traitée en tâche de fond par <see cref="IngestWorker"/>.
/// </summary>
public sealed partial class MediaLibrary(
    IDbContextFactory<NarDbContext> dbFactory,
    SmbSources smb,
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

            // Liste des fichiers vidéo : disque local, ou partage Windows lu par le réseau.
            List<(string Path, string Name, long Size, DateTime WriteUtc)> files;
            try
            {
                if (folder.Source == FolderSource.Jellyfin)
                {
                    var result = await ScanJellyfinAsync(db, folder, ct);
                    folder.LastScanError = null;
                    folder.LastScanAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(ct);
                    if (result.Added + result.Updated > 0) WorkAvailable?.Invoke();
                    Changed?.Invoke();
                    return result;
                }
                files = folder.Source == FolderSource.Smb
                    ? await Task.Run(() => ListSmb(folder), ct)
                    : ListLocal(folder);
                folder.LastScanError = null;
            }
            catch (Exception ex) when (ex is SmbException or JellyfinException or IOException or UnauthorizedAccessException or System.Net.Sockets.SocketException)
            {
                // Dossier injoignable (PC éteint…) : on ne déclare surtout pas tous ses médias absents.
                folder.LastScanError = ex.Message.Length > 300 ? ex.Message[..300] : ex.Message;
                folder.LastScanAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                logger.LogWarning("Médiathèque {Path} injoignable : {Error}", folder.Path, ex.Message);
                Changed?.Invoke();
                throw new IOException(folder.LastScanError, ex);
            }

            var known = await db.MediaAssets.Where(a => a.FolderId == folder.Id).ToDictionaryAsync(a => a.Path, ct);
            int added = 0, updated = 0, missing = 0;
            var seen = new HashSet<string>();

            foreach (var file in files)
            {
                seen.Add(file.Path);
                if (known.TryGetValue(file.Path, out var asset))
                {
                    if (asset.FileSize != file.Size || asset.FileModifiedUtc != file.WriteUtc || asset.Status == AssetStatus.Absent)
                    {
                        asset.FileSize = file.Size;
                        asset.FileModifiedUtc = file.WriteUtc;
                        asset.Status = AssetStatus.EnAttente;
                        updated++;
                    }
                    continue;
                }

                // Un fichier déjà connu via un autre dossier n'est pas dupliqué.
                if (await db.MediaAssets.AnyAsync(a => a.Path == file.Path, ct)) continue;

                db.MediaAssets.Add(new MediaAsset
                {
                    FolderId = folder.Id,
                    Path = file.Path,
                    FileName = file.Name,
                    Title = TitleFromFileName(file.Name),
                    Kind = GuessKind(file.Name, folder.DefaultKind),
                    FileSize = file.Size,
                    FileModifiedUtc = file.WriteUtc,
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

    /// <summary>
    /// Bibliothèque Jellyfin : un média par film ou épisode, avec ses métadonnées (série, saison,
    /// épisode, résumé, année, classification). Titre : « Série — S01E02 — Épisode » pour les épisodes.
    /// </summary>
    private async Task<(int Added, int Updated, int Missing)> ScanJellyfinAsync(NarDbContext db, MediaFolder folder, CancellationToken ct)
    {
        var client = smb.JellyfinOf(folder);
        var items = await client.ItemsAsync(folder.RemoteLibraryId ?? "", ct);
        var known = await db.MediaAssets.Where(a => a.FolderId == folder.Id).ToDictionaryAsync(a => a.Path, ct);
        var seen = new HashSet<string>();
        int added = 0, updated = 0, missing = 0;

        foreach (var item in items)
        {
            var path = SmbSources.JellyfinItemPath(folder.Id, item.Id);
            seen.Add(path);
            if (!known.TryGetValue(path, out var asset))
            {
                asset = new MediaAsset { FolderId = folder.Id, Path = path, Kind = KindOf(item, folder.DefaultKind) };
                db.MediaAssets.Add(asset);
                added++;
            }
            else if (asset.FileSize != item.Size || asset.FileModifiedUtc != item.ChangedUtc || asset.Status == AssetStatus.Absent)
            {
                asset.Status = AssetStatus.EnAttente;
                updated++;
            }

            asset.ExternalId = item.Id;
            asset.FileName = $"{item.Name}.{item.Container?.Split(',')[0] ?? "mkv"}";
            asset.FileSize = item.Size;
            asset.FileModifiedUtc = item.ChangedUtc;
            asset.SeriesName = item.SeriesName;
            asset.SeasonNumber = item.SeasonNumber;
            asset.EpisodeNumber = item.EpisodeNumber;
            asset.EpisodeTitle = item.Type == "Episode" ? item.Name : null;
            asset.Year = item.Year;
            asset.Overview = item.Overview is { Length: > 2000 } o ? o[..2000] : item.Overview;
            asset.Rating = item.Rating;
            asset.Genres = item.Genres.Length == 0 ? null : string.Join(", ", item.Genres);
            asset.Title = item.Type == "Episode" && item.SeriesName is not null
                ? $"{item.SeriesName} — S{item.SeasonNumber ?? 0:00}E{item.EpisodeNumber ?? 0:00} — {item.Name}"
                : item.Year is { } y ? $"{item.Name} ({y})" : item.Name;
            if (asset.Title.Length > 300) asset.Title = asset.Title[..300];
        }

        foreach (var asset in known.Values.Where(a => !seen.Contains(a.Path) && a.Status != AssetStatus.Absent))
        {
            asset.Status = AssetStatus.Absent;
            asset.Error = "Retiré de Jellyfin";
            missing++;
        }

        folder.LastScanCount = items.Count;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Jellyfin « {Library} » : {Added} ajouté(s), {Updated} modifié(s), {Missing} retiré(s)",
            folder.RemoteLibraryName, added, updated, missing);
        return (added, updated, missing);
    }

    private static AssetKind KindOf(JellyfinItem item, AssetKind fallback) => item.Type switch
    {
        "Movie" => AssetKind.Film,
        "Episode" => AssetKind.Episode,
        "MusicVideo" => AssetKind.Clip,
        _ => fallback,
    };

    private static List<(string, string, long, DateTime)> ListLocal(MediaFolder folder)
    {
        if (!Directory.Exists(folder.Path)) throw new IOException($"Dossier introuvable : {folder.Path}");
        return Directory.EnumerateFiles(folder.Path, "*",
                new EnumerationOptions { RecurseSubdirectories = folder.Recursive, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System })
            .Where(f => VideoExtensions.Contains(Path.GetExtension(f)))
            .Select(f => new FileInfo(f))
            .Select(f => (f.FullName, f.Name, f.Length, f.LastWriteTimeUtc))
            .ToList();
    }

    private List<(string, string, long, DateTime)> ListSmb(MediaFolder folder)
    {
        var target = smb.TargetOf(folder);
        using var session = SmbSession.Open(target);
        var root = SmbSources.FolderPath(target.Host, target.Share, null);
        return session.EnumerateFiles(folder.SmbSubPath ?? "", folder.Recursive, name => VideoExtensions.Contains(Path.GetExtension(name)))
            .Select(e => ($"{root}/{e.RelativePath}", e.Name, e.Size, e.LastWriteUtc))
            .ToList();
    }

    public async Task ScanAllAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var ids = await db.MediaFolders.Where(f => f.Enabled).Select(f => f.Id).ToListAsync(ct);
        foreach (var id in ids)
        {
            try { await ScanFolderAsync(id, ct); }
            catch (IOException) { /* déjà journalisé : dossier injoignable */ }
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
