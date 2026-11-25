using System.Diagnostics;

namespace NAR.Engine.Playout;

/// <summary>Lancement d'un processus FFmpeg avec flux binaires et journal d'erreurs drainé.</summary>
internal static class FfmpegProcess
{
    public static Process Start(string ffmpegPath, IEnumerable<string> args, bool redirectInput, bool redirectOutput,
        ILogger logger, string tag, Action<string>? onErrorLine = null)
    {
        var psi = new ProcessStartInfo(ffmpegPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = redirectInput,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        logger.LogDebug("[{Tag}] ffmpeg {Args}", tag, string.Join(' ', psi.ArgumentList));
        var process = Process.Start(psi) ?? throw new InvalidOperationException($"Impossible de lancer {ffmpegPath}");

        // stderr doit toujours être vidé, sinon FFmpeg se bloque quand le tampon est plein.
        _ = Task.Run(async () =>
        {
            try
            {
                while (await process.StandardError.ReadLineAsync() is { } line)
                {
                    if (line.Length == 0) continue;
                    onErrorLine?.Invoke(line);
                    logger.LogDebug("[{Tag}] {Line}", tag, line);
                }
            }
            catch (Exception) { /* processus terminé */ }
        });
        return process;
    }

    public static void Kill(Process? process)
    {
        if (process is null) return;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception) { /* déjà terminé */ }
    }
}
