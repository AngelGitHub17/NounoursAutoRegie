using System.Globalization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NAR.Server.Data;

namespace NAR.Server.Services.Network;

/// <summary>
/// Fait le lien entre la médiathèque et les sources distantes (partages Windows, Jellyfin) :
/// identifiants chiffrés, et adresse de lecture locale pour FFmpeg.
/// FFmpeg (analyse et antenne) lit ces médias via un relais HTTP local du serveur
/// (/interne/smb/… ou /interne/jellyfin/…), qui gère les sauts dans le fichier (Range)
/// et garde les identifiants hors des adresses.
/// </summary>
public sealed class SmbSources(
    IDataProtectionProvider protection,
    IDbContextFactory<NarDbContext> dbFactory,
    IHttpClientFactory httpFactory,
    IOptions<NarOptions> options)
{
    public const string Scheme = "smb://";
    public const string JellyfinScheme = "jellyfin://";
    private readonly IDataProtector _protector = protection.CreateProtector("NAR.Smb.Password.v1");

    public string Protect(string secret) => _protector.Protect(secret);
    public string Unprotect(string? protectedSecret) => protectedSecret is { Length: > 0 } p ? _protector.Unprotect(p) : "";

    public SmbTarget TargetOf(MediaFolder folder) => new(
        folder.SmbHost ?? throw new InvalidOperationException("Hôte manquant"),
        folder.SmbShare ?? throw new InvalidOperationException("Partage manquant"),
        folder.SmbUser ?? "",
        Unprotect(folder.SmbPasswordProtected),
        folder.SmbDomain);

    public JellyfinClient JellyfinOf(MediaFolder folder) =>
        new(httpFactory.CreateClient("jellyfin"), folder.RemoteUrl ?? throw new InvalidOperationException("Adresse Jellyfin manquante"),
            Unprotect(folder.RemoteKeyProtected));

    public JellyfinClient Jellyfin(string url, string apiKey) => new(httpFactory.CreateClient("jellyfin"), url, apiKey);

    public static string FolderPath(string host, string share, string? subPath) =>
        $"{Scheme}{host}/{share}" + (string.IsNullOrWhiteSpace(subPath) ? "" : "/" + subPath.Replace('\\', '/').Trim('/'));

    public static string JellyfinFolderPath(string baseUrl, string libraryId) =>
        $"{JellyfinScheme}{new Uri(baseUrl).Authority}/{libraryId}";

    public static string JellyfinItemPath(int folderId, string itemId) => $"{JellyfinScheme}{folderId}/{itemId}";

    public static bool IsSmb(string path) => path.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase);
    public static bool IsJellyfin(string path) => path.StartsWith(JellyfinScheme, StringComparison.OrdinalIgnoreCase);
    public static bool IsRemote(string path) => IsSmb(path) || IsJellyfin(path);

    /// <summary>
    /// Chemin à donner à FFmpeg : le fichier lui-même, ou le relais local pour une source distante.
    /// La date de modification fait partie de l'adresse : un fichier remplacé change d'adresse.
    /// </summary>
    public string Playable(MediaAsset asset)
    {
        if (!IsRemote(asset.Path)) return asset.Path;
        var kind = IsSmb(asset.Path) ? "smb" : "jellyfin";
        var name = string.IsNullOrEmpty(asset.FileName) ? "media" : asset.FileName;
        return string.Create(CultureInfo.InvariantCulture,
            $"http://127.0.0.1:{options.Value.HttpPort}/interne/{kind}/{asset.Id}/{Uri.EscapeDataString(name)}?v={asset.FileModifiedUtc.Ticks}");
    }

    /// <summary>Chemin relatif du fichier dans le partage, d'après smb://hôte/partage/chemin.</summary>
    public static string RelativeInShare(MediaAsset asset)
    {
        var rest = asset.Path[Scheme.Length..];
        var parts = rest.Split('/', 3);
        return parts.Length == 3 ? parts[2] : "";
    }

    /// <summary>Identifiant Jellyfin de l'élément, d'après jellyfin://dossier/élément.</summary>
    public static string JellyfinItemId(MediaAsset asset) => asset.ExternalId ?? asset.Path.Split('/').Last();

    public async Task<(MediaAsset Asset, MediaFolder Folder)?> RemoteAssetAsync(int assetId, FolderSource source, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var asset = await db.MediaAssets.AsNoTracking().Include(a => a.Folder).FirstOrDefaultAsync(a => a.Id == assetId, ct);
        if (asset?.Folder is not { } folder || folder.Source != source) return null;
        return (asset, folder);
    }

    public async Task<(MediaAsset Asset, SmbTarget Target)?> ForAssetAsync(int assetId, CancellationToken ct)
    {
        var found = await RemoteAssetAsync(assetId, FolderSource.Smb, ct);
        if (found is not { } f || !IsSmb(f.Asset.Path)) return null;
        return (f.Asset, TargetOf(f.Folder));
    }
}
