using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NAR.Server.Data;

namespace NAR.Server.Services;

/// <summary>
/// Premier démarrage : si le dossier de données contient des médias de test (créés par
/// l'installeur), on crée une médiathèque et une chaîne de démonstration prête à l'antenne.
/// </summary>
public sealed class Seeder(
    IDbContextFactory<NarDbContext> dbFactory,
    MediaLibrary library,
    IOptions<NarOptions> options,
    ILogger<Seeder> logger)
{
    public async Task SeedAsync()
    {
        var o = options.Value;
        if (!o.SeedTestChannel) return;

        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.Channels.AnyAsync() || await db.MediaFolders.AnyAsync()) return;

        var mediaDir = Path.Combine(o.ResolvedDataDirectory, "media");
        if (!Directory.Exists(mediaDir) || !Directory.EnumerateFiles(mediaDir).Any()) return;

        var folder = new MediaFolder { Path = mediaDir, DefaultKind = AssetKind.Programme };
        db.MediaFolders.Add(folder);
        await db.SaveChangesAsync();
        await library.ScanFolderAsync(folder.Id);

        var assets = await db.MediaAssets.Where(a => a.FolderId == folder.Id).ToListAsync();
        MediaAsset? Find(string name) => assets.FirstOrDefault(a => a.FileName.StartsWith(name, StringComparison.OrdinalIgnoreCase));

        var channel = new Channel
        {
            Name = "NAR Test",
            Number = 1,
            Enabled = true,
            Loop = true,
            FillerAssetId = Find("bouche-trou")?.Id,
        };
        db.Channels.Add(channel);
        await db.SaveChangesAsync();

        var position = 0;
        foreach (var name in new[] { "jingle", "programme", "spot-sans-son", "bande-annonce" })
        {
            if (Find(name) is { } asset)
                db.PlaylistEntries.Add(new PlaylistEntry { ChannelId = channel.Id, AssetId = asset.Id, Position = position++ });
        }
        await db.SaveChangesAsync();
        logger.LogInformation("Chaîne de démonstration « {Name} » créée avec {Count} élément(s)", channel.Name, position);
    }
}
