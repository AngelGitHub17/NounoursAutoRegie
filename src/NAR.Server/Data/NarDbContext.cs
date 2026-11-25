using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace NAR.Server.Data;

public class NarDbContext(DbContextOptions<NarDbContext> options) : DbContext(options)
{
    public DbSet<Channel> Channels => Set<Channel>();
    public DbSet<MediaFolder> MediaFolders => Set<MediaFolder>();
    public DbSet<MediaAsset> MediaAssets => Set<MediaAsset>();
    public DbSet<PlaylistEntry> PlaylistEntries => Set<PlaylistEntry>();
    public DbSet<AppUser> Users => Set<AppUser>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Channel>(e =>
        {
            e.HasIndex(x => x.Number);
            e.HasOne(x => x.FillerAsset).WithMany().HasForeignKey(x => x.FillerAssetId).OnDelete(DeleteBehavior.SetNull);
            e.HasMany(x => x.Entries).WithOne(x => x.Channel!).HasForeignKey(x => x.ChannelId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<MediaFolder>(e => e.HasIndex(x => x.Path).IsUnique());
        b.Entity<MediaAsset>(e =>
        {
            e.HasIndex(x => x.Path).IsUnique();
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.Kind);
            e.HasOne(x => x.Folder).WithMany().HasForeignKey(x => x.FolderId).OnDelete(DeleteBehavior.SetNull);
        });
        b.Entity<PlaylistEntry>(e =>
        {
            e.HasIndex(x => new { x.ChannelId, x.Position });
            e.HasIndex(x => x.Key).IsUnique();
            e.HasOne(x => x.Asset).WithMany().HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<AppUser>(e => e.HasIndex(x => x.UserName).IsUnique());
    }
}

/// <summary>Pour les outils EF (dotnet ef migrations add …).</summary>
public class NarDbContextFactory : IDesignTimeDbContextFactory<NarDbContext>
{
    public NarDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<NarDbContext>().UseSqlite("Data Source=design.db").Options);
}
