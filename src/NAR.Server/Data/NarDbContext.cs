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
    public DbSet<AtelierSound> AtelierSounds => Set<AtelierSound>();
    public DbSet<AtelierCreation> AtelierCreations => Set<AtelierCreation>();
    public DbSet<ProgrammeType> ProgrammeTypes => Set<ProgrammeType>();
    public DbSet<ProgrammeTypeBloc> ProgrammeTypeBlocs => Set<ProgrammeTypeBloc>();
    public DbSet<SeriesProgress> SeriesProgress => Set<SeriesProgress>();

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
            e.HasIndex(x => x.SeriesName);
            e.HasOne(x => x.Folder).WithMany().HasForeignKey(x => x.FolderId).OnDelete(DeleteBehavior.SetNull);
        });
        b.Entity<PlaylistEntry>(e =>
        {
            e.HasIndex(x => new { x.ChannelId, x.Position });
            e.HasIndex(x => x.Key).IsUnique();
            e.HasOne(x => x.Asset).WithMany().HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<AppUser>(e => e.HasIndex(x => x.UserName).IsUnique());
        b.Entity<AtelierCreation>(e => e.HasOne(x => x.Sound).WithMany().HasForeignKey(x => x.SoundId).OnDelete(DeleteBehavior.SetNull));
        b.Entity<ProgrammeType>(e => e.HasMany(x => x.Blocs).WithOne(x => x.ProgrammeType!).HasForeignKey(x => x.ProgrammeTypeId).OnDelete(DeleteBehavior.Cascade));
        b.Entity<ProgrammeTypeBloc>(e => e.HasOne(x => x.Asset).WithMany().HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.SetNull));
        b.Entity<SeriesProgress>(e => e.HasIndex(x => new { x.ChannelId, x.SeriesName }).IsUnique());
    }
}

/// <summary>Pour les outils EF (dotnet ef migrations add …).</summary>
public class NarDbContextFactory : IDesignTimeDbContextFactory<NarDbContext>
{
    public NarDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<NarDbContext>().UseSqlite("Data Source=design.db").Options);
}
