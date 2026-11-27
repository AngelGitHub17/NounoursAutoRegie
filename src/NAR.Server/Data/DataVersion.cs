using Microsoft.EntityFrameworkCore.Diagnostics;

namespace NAR.Server.Data;

/// <summary>
/// Compteur incrémenté à chaque enregistrement en base : permet de réutiliser ce qui en est déduit
/// (configuration des moteurs…) tant que rien n'a changé, au lieu de tout reconstruire toutes les 2 s.
/// </summary>
public sealed class DataVersion : SaveChangesInterceptor
{
    private long _value;

    public long Value => Interlocked.Read(ref _value);

    public void Bump() => Interlocked.Increment(ref _value);

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        if (result > 0) Bump();
        return result;
    }

    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        if (result > 0) Bump();
        return ValueTask.FromResult(result);
    }
}
