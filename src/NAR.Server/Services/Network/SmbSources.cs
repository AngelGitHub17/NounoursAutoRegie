using System.Globalization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NAR.Server.Data;

namespace NAR.Server.Services.Network;

/// <summary>
/// Fait le lien entre la médiathèque et les partages Windows :
/// identifiants chiffrés, et adresse de lecture locale pour FFmpeg.
/// FFmpeg (analyse et antenne) lit les fichiers d'un partage via un relais HTTP local
/// du serveur (/interne/smb/…), qui gère les sauts dans le fichier (requêtes Range).
/// </summary>
public sealed class SmbSources(
    IDataProtectionProvider protection,
    IDbContextFactory<NarDbContext> dbFactory,
    IOptions<NarOptions> options)
{
    public const string Scheme = "smb://";
    private readonly IDataProtector _protector = protection.CreateProtector("NAR.Smb.Password.v1");

    public string Protect(string password) => _protector.Protect(password);

    public SmbTarget TargetOf(MediaFolder folder) => new(
        folder.SmbHost ?? throw new InvalidOperationException("Hôte manquant"),
        folder.SmbShare ?? throw new InvalidOperationException("Partage manquant"),
        folder.SmbUser ?? "",
        folder.SmbPasswordProtected is { Length: > 0 } p ? _protector.Unprotect(p) : "",
        folder.SmbDomain);

    public static string FolderPath(string host, string share, string? subPath) =>
        $"{Scheme}{host}/{share}" + (string.IsNullOrWhiteSpace(subPath) ? "" : "/" + subPath.Replace('\\', '/').Trim('/'));

    public static bool IsSmb(string path) => path.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Chemin à donner à FFmpeg : le fichier lui-même, ou le relais local pour un partage.
    /// La date de modification fait partie de l'adresse : un fichier remplacé change d'adresse.
    /// </summary>
    public string Playable(MediaAsset asset) =>
        IsSmb(asset.Path)
            ? string.Create(CultureInfo.InvariantCulture,
                $"http://127.0.0.1:{options.Value.HttpPort}/interne/smb/{asset.Id}/{Uri.EscapeDataString(asset.FileName)}?v={asset.FileModifiedUtc.Ticks}")
            : asset.Path;

    /// <summary>Chemin relatif du fichier dans le partage, d'après smb://hôte/partage/chemin.</summary>
    public static string RelativeInShare(MediaAsset asset)
    {
        var rest = asset.Path[Scheme.Length..];
        var parts = rest.Split('/', 3);
        return parts.Length == 3 ? parts[2] : "";
    }

    public async Task<(MediaAsset Asset, SmbTarget Target)?> ForAssetAsync(int assetId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var asset = await db.MediaAssets.AsNoTracking().Include(a => a.Folder).FirstOrDefaultAsync(a => a.Id == assetId, ct);
        if (asset?.Folder is not { Source: FolderSource.Smb } folder || !IsSmb(asset.Path)) return null;
        return (asset, TargetOf(folder));
    }
}
