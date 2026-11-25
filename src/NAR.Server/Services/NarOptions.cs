namespace NAR.Server.Services;

/// <summary>Réglages de NAR.Server (section « Nar » de appsettings.json ou variables Nar__…).</summary>
public sealed class NarOptions
{
    /// <summary>Dossier des données : base, vignettes, HLS et journaux des chaînes. Relatif au dossier du serveur.</summary>
    public string DataDirectory { get; set; } = "../data";

    /// <summary>Port HTTP de la régie (et du flux HLS public /live/…).</summary>
    public int HttpPort { get; set; } = 8090;

    /// <summary>Exécutable du moteur. Vide = recherche automatique à côté du serveur.</summary>
    /// <summary>
    /// Port « diffusion » à ouvrir sur Internet : il ne sert que les flux /live/… et exige la clé
    /// de diffusion. La régie (HttpPort) reste ainsi sur le réseau local. 0 = désactivé.
    /// </summary>
    public int PublicPort { get; set; } = 8091;

    public string? EnginePath { get; set; }

    public string FfmpegPath { get; set; } = "ffmpeg";
    public string FfprobePath { get; set; } = "ffprobe";

    /// <summary>Premier port local attribué aux moteurs (port = base + id de la chaîne).</summary>
    public int EngineBasePort { get; set; } = 8100;

    /// <summary>Crée une chaîne de démonstration au premier démarrage si des médias de test existent.</summary>
    public bool SeedTestChannel { get; set; } = true;

    /// <summary>Intervalle de rescan automatique des dossiers de la médiathèque.</summary>
    public int RescanMinutes { get; set; } = 10;

    public string ResolvedDataDirectory =>
        Path.GetFullPath(Path.IsPathRooted(DataDirectory) ? DataDirectory : Path.Combine(AppContext.BaseDirectory, DataDirectory));

    public string ThumbnailDirectory => Path.Combine(ResolvedDataDirectory, "thumbs");
    public string ChannelDirectory(int channelId) => Path.Combine(ResolvedDataDirectory, "channels", channelId.ToString());
}
