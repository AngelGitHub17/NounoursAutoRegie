using System.Globalization;

namespace NAR.Core.Media;

/// <summary>
/// Cadence d'image exprimée en fraction exacte (25/1, 50/1, 30000/1001…).
/// Toutes les durées de NAR sont comptées en images à partir de cette fraction,
/// jamais en secondes flottantes, pour éviter toute dérive sur 24 h.
/// </summary>
public readonly record struct FrameRate
{
    public int Numerator { get; }
    public int Denominator { get; }

    public FrameRate(int numerator, int denominator = 1)
    {
        if (numerator <= 0 || denominator <= 0)
            throw new ArgumentOutOfRangeException(nameof(numerator), "La cadence doit être positive.");
        Numerator = numerator;
        Denominator = denominator;
    }

    public double FramesPerSecond => (double)Numerator / Denominator;

    /// <summary>Nombre d'images entier le plus proche (25, 50, 30 pour 29.97) — utile pour les timecodes et les GOP.</summary>
    public int NominalFps => (int)Math.Round(FramesPerSecond, MidpointRounding.AwayFromZero);

    /// <summary>Accepte "25", "50", "30000/1001", "29.97", "59.94".</summary>
    public static FrameRate Parse(string text)
    {
        text = text.Trim();
        var slash = text.IndexOf('/');
        if (slash > 0)
            return new FrameRate(
                int.Parse(text[..slash], CultureInfo.InvariantCulture),
                int.Parse(text[(slash + 1)..], CultureInfo.InvariantCulture));

        var value = double.Parse(text, CultureInfo.InvariantCulture);
        foreach (var ntsc in new[] { 24, 30, 60 })
        {
            if (Math.Abs(value - ntsc * 1000.0 / 1001.0) < 0.01)
                return new FrameRate(ntsc * 1000, 1001);
        }
        if (Math.Abs(value - Math.Round(value)) < 1e-9)
            return new FrameRate((int)Math.Round(value));
        return new FrameRate((int)Math.Round(value * 1000), 1000);
    }

    /// <summary>Nombre d'images entières contenues dans une durée (arrondi inférieur).</summary>
    public long FramesFloor(TimeSpan duration) =>
        (long)Int128.DivRem((Int128)duration.Ticks * Numerator, (Int128)TimeSpan.TicksPerSecond * Denominator).Quotient;

    /// <summary>Numéro d'image le plus proche d'un instant (utilisé pour les horaires fixes).</summary>
    public long FramesRound(TimeSpan time)
    {
        var num = (Int128)time.Ticks * Numerator * 2 + (Int128)TimeSpan.TicksPerSecond * Denominator;
        var den = (Int128)TimeSpan.TicksPerSecond * Denominator * 2;
        var q = num / den;
        if (num < 0 && q * den != num) q -= 1; // arrondi vers -infini pour les instants passés
        return (long)q;
    }

    public TimeSpan TimeFromFrames(long frames) =>
        TimeSpan.FromTicks((long)((Int128)frames * Denominator * TimeSpan.TicksPerSecond / Numerator));

    /// <summary>
    /// Nombre d'échantillons audio à associer à l'image <paramref name="frame"/>.
    /// En 29.97, les images portent 1601 ou 1602 échantillons à 48 kHz : le cumul reste exact.
    /// </summary>
    public int SamplesForFrame(long frame, int sampleRate) =>
        (int)(CumulativeSamples(frame + 1, sampleRate) - CumulativeSamples(frame, sampleRate));

    private Int128 CumulativeSamples(long frames, int sampleRate) =>
        (Int128)frames * sampleRate * Denominator / Numerator;

    /// <summary>Timecode HH:MM:SS:FF (non drop-frame) pour l'affichage.</summary>
    public string ToTimecode(long frames)
    {
        var fps = NominalFps;
        var ff = frames % fps;
        var totalSeconds = frames / fps;
        return string.Create(CultureInfo.InvariantCulture,
            $"{totalSeconds / 3600:00}:{totalSeconds / 60 % 60:00}:{totalSeconds % 60:00}:{ff:00}");
    }

    public override string ToString() =>
        Denominator == 1
            ? Numerator.ToString(CultureInfo.InvariantCulture)
            : string.Create(CultureInfo.InvariantCulture, $"{Numerator}/{Denominator}");
}
