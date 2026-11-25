using NAR.Core.Status;

namespace NAR.Engine.Playout;

public enum AntennaState
{
    Starting,
    Playing,
    Filler,
    Slate,
    Hold,
}

public sealed class EngineStatus
{
    private EngineStatusSnapshot _snapshot = new("", "", nameof(AntennaState.Starting), 0, "00:00:00:00",
        DateTimeOffset.Now, null, null, null, null, null, null, 0, 0, 0, 0, 0, 0, null);

    public EngineStatusSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public void Publish(EngineStatusSnapshot snapshot) => Volatile.Write(ref _snapshot, snapshot);
}
