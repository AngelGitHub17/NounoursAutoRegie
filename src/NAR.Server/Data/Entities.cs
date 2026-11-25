using System.ComponentModel.DataAnnotations;

namespace NAR.Server.Data;

public enum AssetKind
{
    Programme,
    Episode,
    Film,
    Spot,
    Jingle,
    BandeAnnonce,
    Habillage,
    BoucheTrou,
    Clip,
    Autre,
}

public enum AssetStatus
{
    EnAttente,
    Analyse,
    Pret,
    Erreur,
    Absent,
}

public enum StartMode
{
    Enchaine,
    HeureFixe,
}

/// <summary>Une chaîne de télévision : son format d'antenne, son encodage et son conducteur.</summary>
public class Channel
{
    public int Id { get; set; }
    [MaxLength(80)] public string Name { get; set; } = "";
    public int Number { get; set; }

    /// <summary>Antenne allumée : le superviseur fait tourner son moteur.</summary>
    public bool Enabled { get; set; }

    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    [MaxLength(16)] public string FrameRate { get; set; } = "25";
    [MaxLength(32)] public string VideoEncoder { get; set; } = "libx264";
    [MaxLength(32)] public string? EncoderPreset { get; set; } = "veryfast";
    public int VideoBitrateKbps { get; set; } = 6000;
    public int AudioBitrateKbps { get; set; } = 160;
    public int SegmentSeconds { get; set; } = 2;

    /// <summary>Port local du moteur (statut + HLS, joint par NAR.Server uniquement).</summary>
    public int EnginePort { get; set; }

    public double PreloadSeconds { get; set; } = 10;
    public bool Loop { get; set; }

    public int? FillerAssetId { get; set; }
    public MediaAsset? FillerAsset { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<PlaylistEntry> Entries { get; set; } = new();
}

public enum FolderSource
{
    /// <summary>Dossier vu directement par le serveur (disque, montage).</summary>
    Local,
    /// <summary>Dossier partagé Windows (SMB), lu par le réseau sans montage.</summary>
    Smb,
    /// <summary>Bibliothèque d'un serveur Jellyfin (films, séries).</summary>
    Jellyfin,
}

/// <summary>Dossier surveillé par la médiathèque.</summary>
public class MediaFolder
{
    public int Id { get; set; }

    /// <summary>Chemin local, ou smb://hôte/partage/sous-dossier pour un partage réseau.</summary>
    [MaxLength(500)] public string Path { get; set; } = "";

    public FolderSource Source { get; set; } = FolderSource.Local;
    [MaxLength(255)] public string? SmbHost { get; set; }
    [MaxLength(120)] public string? SmbShare { get; set; }
    [MaxLength(500)] public string? SmbSubPath { get; set; }
    [MaxLength(120)] public string? SmbUser { get; set; }
    [MaxLength(120)] public string? SmbDomain { get; set; }

    /// <summary>Mot de passe chiffré (Data Protection), jamais en clair dans la base.</summary>
    [MaxLength(2000)] public string? SmbPasswordProtected { get; set; }

    /// <summary>Jellyfin : adresse du serveur, clé API chiffrée, bibliothèque.</summary>
    [MaxLength(300)] public string? RemoteUrl { get; set; }
    [MaxLength(2000)] public string? RemoteKeyProtected { get; set; }
    [MaxLength(64)] public string? RemoteLibraryId { get; set; }
    [MaxLength(200)] public string? RemoteLibraryName { get; set; }

    [MaxLength(300)] public string? LastScanError { get; set; }
    public bool Recursive { get; set; } = true;
    public bool Enabled { get; set; } = true;
    public AssetKind DefaultKind { get; set; } = AssetKind.Programme;
    public DateTimeOffset? LastScanAt { get; set; }
    public int LastScanCount { get; set; }
}

/// <summary>Un média de la médiathèque et le résultat de son analyse.</summary>
public class MediaAsset
{
    public int Id { get; set; }
    public int? FolderId { get; set; }
    public MediaFolder? Folder { get; set; }

    [MaxLength(1000)] public string Path { get; set; } = "";
    [MaxLength(300)] public string FileName { get; set; } = "";
    [MaxLength(300)] public string Title { get; set; } = "";
    public AssetKind Kind { get; set; } = AssetKind.Programme;
    public AssetStatus Status { get; set; } = AssetStatus.EnAttente;
    [MaxLength(1000)] public string? Error { get; set; }

    public long FileSize { get; set; }
    public DateTime FileModifiedUtc { get; set; }

    public TimeSpan Duration { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    [MaxLength(32)] public string? FrameRate { get; set; }
    [MaxLength(32)] public string? VideoCodec { get; set; }
    [MaxLength(32)] public string? AudioCodec { get; set; }
    public bool HasAudio { get; set; }

    /// <summary>Volume intégré mesuré (EBU R128), en LUFS.</summary>
    public double? LoudnessLufs { get; set; }

    /// <summary>Correction appliquée à la diffusion pour atteindre -23 LUFS.</summary>
    public double GainDb { get; set; }

    public bool HasThumbnail { get; set; }

    // Métadonnées éditoriales (Jellyfin, ou saisies) : utilisées par l'EPG et plus tard les grilles.
    [MaxLength(64)] public string? ExternalId { get; set; }
    [MaxLength(300)] public string? SeriesName { get; set; }
    public int? SeasonNumber { get; set; }
    public int? EpisodeNumber { get; set; }
    [MaxLength(300)] public string? EpisodeTitle { get; set; }
    public int? Year { get; set; }
    [MaxLength(2000)] public string? Overview { get; set; }
    [MaxLength(32)] public string? Rating { get; set; }
    [MaxLength(300)] public string? Genres { get; set; }
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? AnalyzedAt { get; set; }
}

/// <summary>Un élément du conducteur d'une chaîne.</summary>
public class PlaylistEntry
{
    public int Id { get; set; }
    public int ChannelId { get; set; }
    public Channel? Channel { get; set; }

    /// <summary>Identifiant stable transmis au moteur (survit aux réordonnancements).</summary>
    [MaxLength(32)] public string Key { get; set; } = Guid.NewGuid().ToString("N");

    public int Position { get; set; }
    public int AssetId { get; set; }
    public MediaAsset? Asset { get; set; }

    [MaxLength(300)] public string? TitleOverride { get; set; }
    public StartMode StartMode { get; set; } = StartMode.Enchaine;
    public DateTimeOffset? FixedStart { get; set; }
    public TimeSpan? InPoint { get; set; }
    public TimeSpan? OutPoint { get; set; }

    public string DisplayTitle => string.IsNullOrWhiteSpace(TitleOverride) ? Asset?.Title ?? "?" : TitleOverride!;

    /// <summary>Durée diffusée (point d'entrée / sortie appliqués).</summary>
    public TimeSpan PlayedDuration
    {
        get
        {
            var total = Asset?.Duration ?? TimeSpan.Zero;
            var end = OutPoint is { } o && o > TimeSpan.Zero && o < total ? o : total;
            var start = InPoint ?? TimeSpan.Zero;
            var d = end - start;
            return d < TimeSpan.Zero ? TimeSpan.Zero : d;
        }
    }
}

/// <summary>Compte local de la régie.</summary>
public class AppUser
{
    public int Id { get; set; }
    [MaxLength(64)] public string UserName { get; set; } = "";
    [MaxLength(120)] public string DisplayName { get; set; } = "";
    [MaxLength(200)] public string PasswordHash { get; set; } = "";
    [MaxLength(32)] public string Role { get; set; } = Roles.Admin;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLoginAt { get; set; }
}

public static class Roles
{
    public const string Admin = "Administrateur";
    public const string Programmateur = "Programmateur";
    public const string Regisseur = "Régisseur";
    public const string Lecture = "Lecture seule";
    public static readonly string[] All = [Admin, Programmateur, Regisseur, Lecture];
}
