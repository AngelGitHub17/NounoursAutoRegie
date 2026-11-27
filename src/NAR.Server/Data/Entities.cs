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
    public int SegmentSeconds { get; set; } = 4;

    /// <summary>Port local du moteur (statut + HLS, joint par NAR.Server uniquement).</summary>
    public int EnginePort { get; set; }

    public double PreloadSeconds { get; set; } = 10;
    public bool Loop { get; set; }

    /// <summary>Logo de la chaîne (logo.png dans son dossier), utilisé par l'atelier d'habillage.</summary>
    public bool HasLogo { get; set; }

    // Habillage incrusté sur l'antenne.
    public bool OverlayLogo { get; set; } = true;
    [MaxLength(16)] public string OverlayCorner { get; set; } = "haut-droite";
    public double OverlayLogoSize { get; set; } = 9;
    public int OverlayLogoOpacity { get; set; } = 85;
    public bool OverlayClock { get; set; }
    public bool OverlayUpNext { get; set; } = true;
    public int OverlayUpNextBefore { get; set; } = 120;
    public bool OverlayRating { get; set; } = true;
    public bool OverlayRatingAlways { get; set; }

    /// <summary>Qualité adaptative : 0 = une seule qualité, 1 = + 360p, 2 = + 720p et 360p.</summary>
    public int QualiteAdaptative { get; set; } = 1;

    /// <summary>Playlist à donner aux lecteurs : master.m3u8 (toutes les qualités) ou index.m3u8.</summary>
    public string PlaylistName => QualiteAdaptative > 0 ? "master.m3u8" : "index.m3u8";

    // Grille de la semaine.
    /// <summary>La régie remplit elle-même le conducteur d'après la grille.</summary>
    public bool GrilleActive { get; set; }
    /// <summary>Nombre de jours programmés à l'avance.</summary>
    public int GrilleHorizonJours { get; set; } = 2;
    /// <summary>Types de médias utilisés pour combler les trous entre deux cases (séparés par des virgules).</summary>
    [MaxLength(200)] public string RemplissageKinds { get; set; } = "BandeAnnonce,Clip";

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

    /// <summary>Signalétique forcée : 0 = tous publics, 10/12/16/18 ; null = d'après la classification (Rating).</summary>
    public int? Signaletique { get; set; }
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

    /// <summary>Case de grille d'origine (« grille:12:202611252050 ») ; null = ajouté à la main.</summary>
    [MaxLength(64)] public string? GroupKey { get; set; }

    /// <summary>Remplissage automatique entre deux cases de la grille.</summary>
    public bool Remplissage { get; set; }

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

public enum CreationStatus
{
    EnAttente,
    EnCours,
    Pret,
    Erreur,
}

/// <summary>Son envoyé dans l'atelier d'habillage, avec son analyse rythmique.</summary>
public class AtelierSound
{
    public int Id { get; set; }
    [MaxLength(200)] public string Name { get; set; } = "";
    [MaxLength(200)] public string FileName { get; set; } = "";
    public double Duration { get; set; }
    public double Bpm { get; set; }
    public int BeatCount { get; set; }

    /// <summary>Analyse complète (temps, forces, attaques, énergie) en JSON.</summary>
    public string AnalysisJson { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Une vidéo d'habillage fabriquée (ou à fabriquer) par l'atelier.</summary>
public class AtelierCreation
{
    public int Id { get; set; }
    public int Kind { get; set; }
    [MaxLength(200)] public string Title { get; set; } = "";
    [MaxLength(200)] public string? Subtitle { get; set; }
    [MaxLength(60)] public string? Label { get; set; }
    [MaxLength(32)] public string PaletteKey { get; set; } = "retro95";
    public int? SoundId { get; set; }
    public AtelierSound? Sound { get; set; }
    public int? ChannelId { get; set; }
    public bool LogoOnBlack { get; set; }
    public double? DurationSeconds { get; set; }
    public int? SourceAssetId { get; set; }
    [MaxLength(300)] public string? ExtractStarts { get; set; }
    public int Seed { get; set; }

    public CreationStatus Status { get; set; } = CreationStatus.EnAttente;
    public double Progress { get; set; }
    [MaxLength(500)] public string? Error { get; set; }
    [MaxLength(300)] public string? OutputFile { get; set; }
    public int? AssetId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
}

/// <summary>Nature d'un bloc de programme type.</summary>
public enum BlocType
{
    /// <summary>Toujours le même média (ouverture d'écran pub, générique de case…).</summary>
    Media,
    /// <summary>Un média tiré au sort parmi un type (film, clip, jingle…).</summary>
    AuHasard,
    /// <summary>Choisi au moment de l'insertion (le film de la soirée…).</summary>
    AuChoix,
    /// <summary>L'épisode suivant d'une série, d'après ce que la chaîne a déjà programmé.</summary>
    EpisodeSuivant,
    /// <summary>Spots tirés au sort jusqu'à la durée voulue.</summary>
    EcranPub,
    /// <summary>Création Rétro 95 de l'atelier liée au programme du modèle (jingle, générique, bande-annonce).</summary>
    HabillageDuProgramme,
}

/// <summary>Programme type : une suite de blocs réutilisable (« Film du soir », « Série du midi »…).</summary>
public class ProgrammeType
{
    public int Id { get; set; }
    [MaxLength(100)] public string Name { get; set; } = "";
    [MaxLength(500)] public string? Description { get; set; }
    public List<ProgrammeTypeBloc> Blocs { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class ProgrammeTypeBloc
{
    public int Id { get; set; }
    public int ProgrammeTypeId { get; set; }
    public ProgrammeType? ProgrammeType { get; set; }
    public int Position { get; set; }
    public BlocType Type { get; set; }

    /// <summary>Libellé affiché (« Film », « Pub »…).</summary>
    [MaxLength(60)] public string? Label { get; set; }

    /// <summary>Média fixe (bloc Media).</summary>
    public int? AssetId { get; set; }
    public MediaAsset? Asset { get; set; }

    /// <summary>Type de média (AuHasard, AuChoix).</summary>
    public AssetKind? Kind { get; set; }

    /// <summary>Série (EpisodeSuivant) ; vide = choisie à l'insertion.</summary>
    [MaxLength(300)] public string? SeriesName { get; set; }

    /// <summary>Nombre d'épisodes à la suite (EpisodeSuivant).</summary>
    public int Count { get; set; } = 1;

    /// <summary>Durée visée en secondes (EcranPub).</summary>
    public int? DurationSeconds { get; set; }

    /// <summary>Création de l'atelier voulue (HabillageDuProgramme) : 0 jingle, 2 générique, 3 bande-annonce.</summary>
    public int? AtelierKind { get; set; }
}

/// <summary>Où en est chaque chaîne dans chaque série (pour « épisode suivant »).</summary>
public class SeriesProgress
{
    public int Id { get; set; }
    public int ChannelId { get; set; }
    [MaxLength(300)] public string SeriesName { get; set; } = "";
    public int? LastAssetId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Case de la grille de la semaine : tel programme type, tels jours, à telle heure.</summary>
public class GrilleCase
{
    public int Id { get; set; }
    public int ChannelId { get; set; }
    public Channel? Channel { get; set; }

    /// <summary>Jours de diffusion : lundi = 1, mardi = 2, mercredi = 4… dimanche = 64.</summary>
    public int Jours { get; set; } = 127;

    /// <summary>Heure de début (heure locale).</summary>
    public TimeSpan Heure { get; set; } = new(20, 50, 0);

    public int ProgrammeTypeId { get; set; }
    public ProgrammeType? ProgrammeType { get; set; }

    /// <summary>Série pour les blocs « épisode suivant » sans série fixée.</summary>
    [MaxLength(300)] public string? SeriesName { get; set; }

    /// <summary>Média pour les blocs « au choix » (sinon : tiré au sort dans le type du bloc).</summary>
    public int? AssetId { get; set; }
    public MediaAsset? Asset { get; set; }

    public bool Active { get; set; } = true;

    public static int Bit(DayOfWeek d) => 1 << (((int)d + 6) % 7);
}
