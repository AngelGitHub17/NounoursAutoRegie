using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace NAR.Server.Services;

public sealed record MediaAnalysis(
    bool Ok,
    string? Error,
    TimeSpan Duration,
    int Width,
    int Height,
    string? FrameRate,
    string? VideoCodec,
    string? AudioCodec,
    bool HasAudio);

/// <summary>
/// Outils d'analyse : ffprobe pour la technique, FFmpeg pour le volume (EBU R128) et les vignettes.
/// Les processus tournent en priorité basse pour ne jamais gêner l'antenne.
/// </summary>
public sealed partial class FfmpegTools(IOptions<NarOptions> options, ILogger<FfmpegTools> logger)
{
    public const double TargetLufs = -23.0;
    private readonly NarOptions _options = options.Value;

    public async Task<MediaAnalysis> ProbeAsync(string path, CancellationToken ct)
    {
        var (code, stdout, stderr) = await RunAsync(_options.FfprobePath,
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", path], ct);
        if (code != 0) return Failed(stderr.Trim().Split('\n').LastOrDefault() ?? $"ffprobe code {code}");

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            JsonElement? video = null, audio = null;
            foreach (var s in root.GetProperty("streams").EnumerateArray())
            {
                var type = s.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
                var attached = s.TryGetProperty("disposition", out var disp)
                               && disp.TryGetProperty("attached_pic", out var ap) && ap.GetInt32() == 1;
                if (type == "video" && !attached && video is null) video = s;
                if (type == "audio" && audio is null) audio = s;
            }
            if (video is not { } v) return Failed("Aucune piste vidéo");

            double seconds = 0;
            if (v.TryGetProperty("duration", out var vd)) double.TryParse(vd.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds);
            if (seconds <= 0 && root.TryGetProperty("format", out var f) && f.TryGetProperty("duration", out var fd))
                double.TryParse(fd.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds);
            if (seconds <= 0) return Failed("Durée inconnue");

            string? rate = null;
            if (v.TryGetProperty("avg_frame_rate", out var afr) && afr.GetString() is { } r && r != "0/0") rate = SimplifyRate(r);
            else if (v.TryGetProperty("r_frame_rate", out var rfr)) rate = SimplifyRate(rfr.GetString() ?? "");

            return new MediaAnalysis(true, null, TimeSpan.FromSeconds(seconds),
                v.TryGetProperty("width", out var w) ? w.GetInt32() : 0,
                v.TryGetProperty("height", out var h) ? h.GetInt32() : 0,
                rate,
                v.TryGetProperty("codec_name", out var vc) ? vc.GetString() : null,
                audio is { } a && a.TryGetProperty("codec_name", out var ac) ? ac.GetString() : null,
                audio is not null);
        }
        catch (Exception ex)
        {
            return Failed($"Analyse illisible : {ex.Message}");
        }
    }

    /// <summary>Mesure le volume intégré (LUFS) de la première piste audio.</summary>
    public async Task<double?> MeasureLoudnessAsync(string path, CancellationToken ct)
    {
        var (_, _, stderr) = await RunAsync(_options.FfmpegPath,
            ["-hide_banner", "-nostats", "-nostdin", "-i", path, "-map", "0:a:0", "-af", "ebur128=framelog=quiet", "-f", "null", "-"], ct);
        var matches = IntegratedRegex().Matches(stderr);
        if (matches.Count == 0) return null;
        return double.Parse(matches[^1].Groups[1].Value, CultureInfo.InvariantCulture);
    }

    public static double GainFor(double? lufs) =>
        lufs is { } l && !double.IsInfinity(l) && l > -70 ? Math.Round(Math.Clamp(TargetLufs - l, -12, 12), 1) : 0;

    public async Task<bool> CreateThumbnailAsync(string path, TimeSpan duration, string output, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var at = TimeSpan.FromSeconds(Math.Min(duration.TotalSeconds * 0.1, 30));
        var (code, _, _) = await RunAsync(_options.FfmpegPath,
            ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-ss", at.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture),
             "-i", path, "-frames:v", "1", "-vf", "scale=320:-2", "-q:v", "4", output], ct);
        return code == 0 && File.Exists(output);
    }

    private static string SimplifyRate(string rate)
    {
        var parts = rate.Split('/');
        if (parts.Length == 2 && double.TryParse(parts[0], CultureInfo.InvariantCulture, out var n)
            && double.TryParse(parts[1], CultureInfo.InvariantCulture, out var d) && d > 0)
        {
            var fps = n / d;
            return Math.Abs(fps - Math.Round(fps)) < 0.01
                ? Math.Round(fps).ToString(CultureInfo.InvariantCulture)
                : fps.ToString("0.###", CultureInfo.InvariantCulture);
        }
        return rate;
    }

    private static MediaAnalysis Failed(string error) => new(false, error, TimeSpan.Zero, 0, 0, null, null, null, false);

    private async Task<(int Code, string Stdout, string Stderr)> RunAsync(string exe, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"{exe} n'a pas démarré");
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { /* non supporté */ }
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { }
            throw;
        }
        logger.LogDebug("{Exe} terminé ({Code})", Path.GetFileName(exe), process.ExitCode);
        return (process.ExitCode, await stdout, await stderr);
    }

    [GeneratedRegex(@"I:\s+(-?[\d.]+) LUFS")]
    private static partial Regex IntegratedRegex();
}
