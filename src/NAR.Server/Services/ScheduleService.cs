using Microsoft.EntityFrameworkCore;
using NAR.Core.Scheduling;
using NAR.Server.Data;

namespace NAR.Server.Services;

/// <summary>Un passage prévu à l'antenne, en heure réelle.</summary>
public sealed record UpcomingProgramme(PlaylistEntry Entry, DateTimeOffset Start, DateTimeOffset End, bool OnAir);

/// <summary>
/// Heures de passage réelles d'une chaîne pour les heures à venir : l'élément à l'antenne sert
/// de repère, puis le conducteur se déroule (en boucle : on repart au début ; les horaires fixes
/// ne valent que pour le premier passage, comme dans le moteur). Sert au guide des programmes.
/// </summary>
public sealed class ScheduleService(IDbContextFactory<NarDbContext> dbFactory, EngineMonitor monitor)
{
    private const int MaxProgrammes = 5000;

    public async Task<(Channel Channel, List<UpcomingProgramme> Items)?> UpcomingAsync(int channelId, TimeSpan horizon, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var channel = await db.Channels.AsNoTracking()
            .Include(c => c.Entries.OrderBy(e => e.Position)).ThenInclude(e => e.Asset)
            .FirstOrDefaultAsync(c => c.Id == channelId, ct);
        if (channel is null) return null;

        var entries = channel.Entries.OrderBy(e => e.Position).Where(e => e.Asset is { Status: AssetStatus.Pret }).ToList();
        var result = new List<UpcomingProgramme>();
        if (!channel.Enabled || entries.Count == 0) return (channel, result);

        var now = DateTimeOffset.Now;
        var limit = now + horizon;
        var status = monitor.Get(channelId);

        // Premier tour : à partir de l'élément à l'antenne (ou du début si on ne le connaît pas).
        var anchor = status?.CurrentId is { } id ? entries.FindIndex(e => e.Key == id) : -1;
        DateTimeOffset anchorStart = now;
        if (anchor >= 0 && status is { RemainingSeconds: { } remaining, CurrentDurationSeconds: { } total })
            anchorStart = now - TimeSpan.FromSeconds(total - remaining);
        var order = anchor >= 0
            ? Enumerable.Range(anchor, entries.Count - anchor).Concat(channel.Loop ? Enumerable.Range(0, anchor) : []).ToList()
            : Enumerable.Range(0, entries.Count).ToList();

        var inputs = order.Select((index, k) =>
        {
            var e = entries[index];
            // Un horaire fixe déjà passé ne compte plus (le moteur l'a franchi ou est en boucle).
            DateTimeOffset? fixedStart = k == 0 && (anchor >= 0 || e.StartMode != StartMode.HeureFixe) ? anchorStart
                : e.StartMode == StartMode.HeureFixe && e.FixedStart > anchorStart ? e.FixedStart : null;
            return new PlanInput(e.Key, e.PlayedDuration, fixedStart);
        }).ToList();
        var planned = TimelinePlanner.Plan(inputs);
        var cursor = anchorStart;
        for (var k = 0; k < planned.Count; k++)
        {
            if (planned[k].Start is not { } start || planned[k].Duration <= TimeSpan.Zero) continue;
            var end = start + planned[k].Duration;
            if (end > now - TimeSpan.FromHours(1))
                result.Add(new UpcomingProgramme(entries[order[k]], start, end, k == 0 && anchor >= 0));
            cursor = end;
        }

        // Tours suivants (conducteur en boucle) : tout s'enchaîne.
        var cycleDuration = entries.Aggregate(TimeSpan.Zero, (t, e) => t + e.PlayedDuration);
        if (channel.Loop && cycleDuration > TimeSpan.FromSeconds(1))
        {
            while (cursor < limit && result.Count < MaxProgrammes)
            {
                foreach (var e in entries)
                {
                    var d = e.PlayedDuration;
                    if (d <= TimeSpan.Zero) continue;
                    result.Add(new UpcomingProgramme(e, cursor, cursor + d, false));
                    cursor += d;
                    if (cursor >= limit || result.Count >= MaxProgrammes) break;
                }
            }
        }
        result.RemoveAll(p => p.Start > limit);
        return (channel, result);
    }
}
