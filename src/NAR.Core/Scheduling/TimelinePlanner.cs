namespace NAR.Core.Scheduling;

/// <param name="Id">Identifiant de l'élément.</param>
/// <param name="Duration">Durée diffusée.</param>
/// <param name="FixedStart">Horaire fixe, ou null si l'élément est enchaîné.</param>
public sealed record PlanInput(string Id, TimeSpan Duration, DateTimeOffset? FixedStart);

/// <summary>Horaire prévu d'un élément, tel que la régie l'affiche.</summary>
/// <param name="Start">Heure de début si elle est connue (un horaire fixe la précède).</param>
/// <param name="Offset">Sinon : position depuis le lancement de l'antenne.</param>
/// <param name="Duration">Durée réellement diffusée (raccourcie si l'élément suivant est calé plus tôt).</param>
/// <param name="CutBy">Temps coupé à la fin, parce qu'un horaire fixe arrive avant la fin.</param>
/// <param name="GapBefore">Temps de secours (bouche-trou) avant cet élément, en attendant son horaire fixe.</param>
public sealed record PlannedEntry(
    string Id,
    DateTimeOffset? Start,
    TimeSpan? Offset,
    TimeSpan Duration,
    TimeSpan CutBy,
    TimeSpan GapBefore)
{
    public DateTimeOffset? End => Start + Duration;
}

/// <summary>
/// Calcule les heures prévues d'un conducteur (enchaînements + horaires fixes) et signale
/// les coupures et les trous. Même logique que le moteur, mais en temps plutôt qu'en images.
/// </summary>
public static class TimelinePlanner
{
    public static IReadOnlyList<PlannedEntry> Plan(IReadOnlyList<PlanInput> inputs)
    {
        var result = new List<PlannedEntry>(inputs.Count);
        DateTimeOffset? cursor = null;   // heure de fin de l'élément précédent, si connue
        var offset = TimeSpan.Zero;      // position relative tant qu'aucun horaire n'est connu

        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            var gap = TimeSpan.Zero;
            DateTimeOffset? start;

            if (input.FixedStart is { } fixedStart)
            {
                if (cursor is { } c && result.Count > 0)
                {
                    if (fixedStart < c)
                    {
                        // L'horaire fixe coupe l'élément précédent.
                        var prev = result[^1];
                        var cut = c - fixedStart;
                        var kept = prev.Duration - cut;
                        if (kept < TimeSpan.Zero) kept = TimeSpan.Zero;
                        result[^1] = prev with { Duration = kept, CutBy = prev.CutBy + (prev.Duration - kept) };
                    }
                    else
                    {
                        gap = fixedStart - c;
                    }
                }
                start = fixedStart;
            }
            else
            {
                start = cursor;
            }

            result.Add(new PlannedEntry(input.Id, start, start is null ? offset : null, input.Duration, TimeSpan.Zero, gap));
            if (start is { } s) cursor = s + input.Duration;
            else offset += input.Duration;
        }
        return result;
    }
}
