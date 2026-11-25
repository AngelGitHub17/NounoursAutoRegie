using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace NAR.Engine.Playout;

public sealed record ProbeResult(bool Ok, TimeSpan Duration, bool HasVideo, bool HasAudio, string? Error)
{
    public static ProbeResult Failed(string error) => new(false, TimeSpan.Zero, false, false, error);
}

/// <summary>Analyse rapide d'un média avec ffprobe (durée, présence vidéo / audio).</summary>
public sealed class MediaProbe(string ffprobePath, ILogger<MediaProbe> logger)
{
    public async Task<ProbeResult> ProbeAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            return ProbeResult.Failed($"Fichier introuvable : {path}");

        var psi = new ProcessStartInfo(ffprobePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "-v", "error", "-print_format", "json", "-show_format", "-show_streams", path })
            psi.ArgumentList.Add(a);

        try
        {
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("ffprobe n'a pas démarré");
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            if (process.ExitCode != 0)
                return ProbeResult.Failed((await stderr).Trim());

            using var doc = JsonDocument.Parse(await stdout);
            var root = doc.RootElement;
            bool hasVideo = false, hasAudio = false;
            double duration = 0, videoDuration = 0;

            if (root.TryGetProperty("streams", out var streams))
            {
                foreach (var s in streams.EnumerateArray())
                {
                    var type = s.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
                    // Les pochettes d'album (attached_pic) ne sont pas de la vidéo.
                    var attached = s.TryGetProperty("disposition", out var disp)
                                   && disp.TryGetProperty("attached_pic", out var ap) && ap.GetInt32() == 1;
                    if (type == "video" && !attached)
                    {
                        hasVideo = true;
                        if (videoDuration <= 0 && s.TryGetProperty("duration", out var vd)
                            && double.TryParse(vd.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                            videoDuration = v;
                    }
                    if (type == "audio") hasAudio = true;
                }
            }
            if (root.TryGetProperty("format", out var format)
                && format.TryGetProperty("duration", out var d)
                && double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var secs))
            {
                duration = secs;
            }

            // La durée de la piste vidéo est plus juste que celle du conteneur (l'audio AAC déborde souvent).
            if (videoDuration > 0) duration = videoDuration;
            if (!hasVideo) return ProbeResult.Failed("Aucune piste vidéo");
            if (duration <= 0) return ProbeResult.Failed("Durée inconnue");
            return new ProbeResult(true, TimeSpan.FromSeconds(duration), hasVideo, hasAudio, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Analyse impossible de {Path}", path);
            return ProbeResult.Failed(ex.Message);
        }
    }
}
