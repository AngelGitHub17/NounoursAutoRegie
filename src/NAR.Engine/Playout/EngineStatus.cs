namespace NAR.Engine.Playout;

public enum AntennaState
{
    Starting,
    Playing,
    Filler,
    Slate,
    Hold,
}

/// <summary>Instantané de l'état de la chaîne, lu par la page de statut.</summary>
public sealed record EngineStatusSnapshot(
    string Channel,
    string Format,
    string State,
    long Frame,
    string Timecode,
    DateTimeOffset StartedAt,
    string? CurrentTitle,
    string? CurrentId,
    double? RemainingSeconds,
    string? NextTitle,
    double LateMs,
    int EncoderQueue,
    long UnderrunFrames,
    long SourceFailures,
    long EncoderRestarts,
    string? LastIncident);

public sealed class EngineStatus
{
    private EngineStatusSnapshot _snapshot = new("", "", nameof(AntennaState.Starting), 0, "00:00:00:00",
        DateTimeOffset.Now, null, null, null, null, 0, 0, 0, 0, 0, null);

    public EngineStatusSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public void Publish(EngineStatusSnapshot snapshot) => Volatile.Write(ref _snapshot, snapshot);
}
