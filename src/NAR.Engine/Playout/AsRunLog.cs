using System.Text.Json;

namespace NAR.Engine.Playout;

/// <summary>
/// Journal de diffusion : une ligne JSON par événement réellement passé à l'antenne,
/// dans un fichier par jour (asrun/&lt;chaîne&gt;-AAAAMMJJ.jsonl).
/// </summary>
public sealed class AsRunLog(string directory, string channelName, ILogger logger)
{
    private readonly object _lock = new();

    public void Write(string evt, long frame, string timecode, string? id = null, string? title = null,
        long? framesPlayed = null, string? detail = null)
    {
        var now = DateTimeOffset.Now;
        var line = JsonSerializer.Serialize(new
        {
            time = now.ToString("O"),
            frame,
            timecode,
            evt,
            id,
            title,
            framesPlayed,
            detail,
        });
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(directory);
                var safeName = string.Concat(channelName.Split(Path.GetInvalidFileNameChars()));
                File.AppendAllText(Path.Combine(directory, $"{safeName}-{now:yyyyMMdd}.jsonl"), line + Environment.NewLine);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Journal de diffusion indisponible");
        }
    }
}
