namespace NAR.Core.Status;

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
    double? CurrentDurationSeconds,
    string? NextTitle,
    string? NextId,
    long Revision,
    double LateMs,
    int EncoderQueue,
    long UnderrunFrames,
    long SourceFailures,
    long EncoderRestarts,
    string? LastIncident);
