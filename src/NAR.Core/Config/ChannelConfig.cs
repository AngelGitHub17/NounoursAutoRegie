using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NAR.Core.Config;

/// <summary>
/// Configuration d'une chaîne pour le moteur (phase 0 : fichier JSON ;
/// en phase 1 elle viendra de la base via NAR.Server).
/// </summary>
public sealed class ChannelConfig
{
    public string Name { get; set; } = "NounoursTV";

    /// <summary>Numéro de version du conducteur : change à chaque modification depuis la régie.</summary>
    public long Revision { get; set; }
    public FormatConfig Format { get; set; } = new();
    public EncoderConfig Encoder { get; set; } = new();
    public HlsConfig Hls { get; set; } = new();
    public int StatusPort { get; set; } = 8090;

    /// <summary>Adresse d'écoute de la page de statut / HLS ("0.0.0.0" = tout le réseau, "127.0.0.1" = local).</summary>
    public string StatusBind { get; set; } = "0.0.0.0";
    public string FfmpegPath { get; set; } = "ffmpeg";
    public string FfprobePath { get; set; } = "ffprobe";

    /// <summary>Délai d'ouverture anticipée de l'élément suivant.</summary>
    public double PreloadSeconds { get; set; } = 10;

    /// <summary>Bouche-trou diffusé en boucle quand rien n'est prévu (sinon : mire).</summary>
    public string? Filler { get; set; }

    public string AsRunDirectory { get; set; } = "asrun";
    public PlaylistConfig Playlist { get; set; } = new();

    /// <summary>Dossier du fichier de configuration : les chemins relatifs partent de là.</summary>
    [JsonIgnore]
    public string BaseDirectory { get; set; } = Directory.GetCurrentDirectory();

    public string Resolve(string path) =>
        Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(BaseDirectory, path));

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Empreinte des réglages qui exigent de relancer le moteur (format, encodeur, sorties, ports).
    /// Tout le reste (conducteur, bouche-trou, préchargement) se recharge à chaud.
    /// </summary>
    public string StructuralFingerprint() => JsonSerializer.Serialize(new
    {
        Name, Format, Encoder, Hls, StatusPort, StatusBind, FfmpegPath, FfprobePath, AsRunDirectory,
    }, JsonOptions);

    public static ChannelConfig Parse(string json, string baseDirectory)
    {
        var config = JsonSerializer.Deserialize<ChannelConfig>(json, JsonOptions)
                     ?? throw new InvalidDataException("Configuration vide");
        config.BaseDirectory = baseDirectory;
        return config;
    }

    public static ChannelConfig Load(string path)
    {
        var full = Path.GetFullPath(path);
        var config = JsonSerializer.Deserialize<ChannelConfig>(File.ReadAllText(full), JsonOptions)
                     ?? throw new InvalidDataException($"Configuration vide : {full}");
        config.BaseDirectory = Path.GetDirectoryName(full)!;
        return config;
    }
}

public sealed class FormatConfig
{
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public string FrameRate { get; set; } = "25";
}

public sealed class EncoderConfig
{
    /// <summary>libx264 (processeur), h264_nvenc (NVIDIA), h264_qsv (Intel), h264_amf (AMD), libx265/hevc_nvenc pour la 4K.</summary>
    public string Video { get; set; } = "libx264";
    public string? Preset { get; set; } = "veryfast";
    public int VideoBitrateKbps { get; set; } = 6000;
    public int AudioBitrateKbps { get; set; } = 160;
    public List<string> ExtraArgs { get; set; } = new();
}

public sealed class HlsConfig
{
    public string Directory { get; set; } = "out/hls";
    public int SegmentSeconds { get; set; } = 2;
    public int ListSize { get; set; } = 15;
}

public sealed class PlaylistConfig
{
    /// <summary>Recommencer au début quand le conducteur est fini (pratique pour les tests d'endurance).</summary>
    public bool Loop { get; set; }
    public List<ItemConfig> Items { get; set; } = new();
}

public sealed class ItemConfig
{
    public string? Id { get; set; }
    public string Title { get; set; } = "";
    public string Path { get; set; } = "";

    /// <summary>Point d'entrée "hh:mm:ss(.fff)".</summary>
    public string? In { get; set; }

    /// <summary>Point de sortie "hh:mm:ss(.fff)".</summary>
    public string? Out { get; set; }

    /// <summary>Durée imposée "hh:mm:ss(.fff)" (prioritaire sur Out).</summary>
    public string? Duration { get; set; }

    /// <summary>
    /// Horaire fixe : "20:00:00" (aujourd'hui) ou date complète ISO 8601.
    /// Absent = enchaîné derrière l'élément précédent.
    /// </summary>
    public string? StartAt { get; set; }

    public double GainDb { get; set; }

    public static TimeSpan? ParseTime(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? null
            : TimeSpan.Parse(text, CultureInfo.InvariantCulture);

    public DateTimeOffset? ParseStartAt(DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(StartAt)) return null;
        if (TimeOnly.TryParse(StartAt, CultureInfo.InvariantCulture, out var time) && !StartAt.Contains('-'))
        {
            var local = now.ToLocalTime();
            var date = DateOnly.FromDateTime(local.DateTime);
            var dt = date.ToDateTime(time);
            return new DateTimeOffset(dt, TimeZoneInfo.Local.GetUtcOffset(dt));
        }
        return DateTimeOffset.Parse(StartAt, CultureInfo.InvariantCulture);
    }
}
