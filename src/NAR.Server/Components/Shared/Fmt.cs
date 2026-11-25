using System.Globalization;
using NAR.Server.Data;
using NAR.Server.Services;

namespace NAR.Server.Components.Shared;

/// <summary>Mise en forme commune de la régie (durées, libellés).</summary>
public static class Fmt
{
    public static string Duration(TimeSpan d) =>
        d.TotalHours >= 1 ? $"{(int)d.TotalHours}:{d:mm\\:ss}" : d.ToString(@"mm\:ss", CultureInfo.InvariantCulture);

    public static string Timecode(TimeSpan d) => $"{(int)d.TotalHours:00}:{d:mm\\:ss}";

    public static string Clock(DateTimeOffset t) => t.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    private static readonly string[] Days = ["dim.", "lun.", "mar.", "mer.", "jeu.", "ven.", "sam."];

    /// <summary>Jour abrégé (« jeu. 26 ») si ce n'est pas aujourd'hui, sinon vide.</summary>
    public static string DayIfNotToday(DateTimeOffset t)
    {
        var local = t.ToLocalTime();
        return local.Date == DateTime.Today ? "" : $"{Days[(int)local.DayOfWeek]} {local.Day}";
    }

    public static string Seconds(double? s) => s is { } v ? Duration(TimeSpan.FromSeconds(Math.Max(0, v))) : "—";

    public static bool TryParseTime(string? text, out TimeSpan value)
    {
        value = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text)) return true;
        string[] formats = [@"h\:mm\:ss\.fff", @"h\:mm\:ss", @"m\:ss\.fff", @"m\:ss", @"%s"];
        return TimeSpan.TryParseExact(text.Trim(), formats, CultureInfo.InvariantCulture, out value);
    }

    public static string Kind(AssetKind kind) => kind switch
    {
        AssetKind.Programme => "Programme",
        AssetKind.Episode => "Épisode",
        AssetKind.Film => "Film",
        AssetKind.Spot => "Spot pub",
        AssetKind.Jingle => "Jingle",
        AssetKind.BandeAnnonce => "Bande-annonce",
        AssetKind.Habillage => "Habillage",
        AssetKind.BoucheTrou => "Bouche-trou",
        AssetKind.Clip => "Clip",
        _ => "Autre",
    };

    public static (string Label, string Css) Status(AssetStatus status) => status switch
    {
        AssetStatus.Pret => ("Prêt", "ok"),
        AssetStatus.EnAttente => ("En attente", "info"),
        AssetStatus.Analyse => ("Analyse…", "warn"),
        AssetStatus.Erreur => ("Erreur", "bad"),
        AssetStatus.Absent => ("Absent", "bad"),
        _ => (status.ToString(), "off"),
    };

    public static (string Label, string Css) Antenna(string? state, EngineProcess? engine)
    {
        if (engine is null || engine.State == EngineState.Arrete) return ("Éteinte", "off");
        if (engine.State == EngineState.Erreur) return ("Moteur en erreur", "bad");
        return state switch
        {
            "Playing" => ("À l'antenne", "ok"),
            "Filler" => ("Bouche-trou", "warn"),
            "Hold" => ("Image figée", "warn"),
            "Slate" => ("Mire", "bad"),
            _ => ("Démarrage…", "info"),
        };
    }

    public static string Format(Channel c) => $"{c.Width}×{c.Height} · {c.FrameRate} i/s";
}
