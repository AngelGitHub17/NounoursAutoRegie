using System.Globalization;
using Microsoft.EntityFrameworkCore;
using NAR.Server.Data;

namespace NAR.Server.Services;

/// <summary>Une diffusion prévue par la grille : telle case, tel jour, telle heure.</summary>
public sealed record GrilleOccurrence(GrilleCase Case, DateTimeOffset Start)
{
    public string Key => Grille.KeyOf(Case.Id, Start);
}

/// <summary>Bilan d'un passage de la grille sur le conducteur.</summary>
public sealed record GrilleReport(int Added, int Removed, int Purged, List<string> Warnings)
{
    public string Summary =>
        Added == 0 && Removed == 0 && Purged == 0 ? "Conducteur déjà à jour."
        : string.Join(", ", new[]
        {
            Added > 0 ? $"{Added} case(s) programmée(s)" : null,
            Removed > 0 ? $"{Removed} case(s) à venir refaite(s)" : null,
            Purged > 0 ? $"{Purged} élément(s) passé(s) retiré(s)" : null,
        }.Where(x => x is not null)) + ".";
}

/// <summary>
/// Grille de la semaine : à partir des cases (« Film du soir, tous les jours à 20:50 »), la régie
/// programme le conducteur sur les jours à venir. Chaque case devient un groupe d'éléments dont le
/// premier est à heure fixe ; le trou avant la case est comblé par du remplissage (bandes-annonces,
/// clips…) qui tient dans le temps disponible, le reste éventuel étant couvert par le bouche-trou.
/// Les éléments passés sont retirés au fil de l'eau pour garder un conducteur léger.
/// </summary>
public sealed class Grille(IDbContextFactory<NarDbContext> dbFactory, ProgrammeTypes types, ILogger<Grille> logger)
{
    private static readonly string[] JourCourt = ["Lun", "Mar", "Mer", "Jeu", "Ven", "Sam", "Dim"];
    private static readonly SemaphoreSlim Lock = new(1, 1);

    public static string KeyOf(int caseId, DateTimeOffset start) =>
        $"grille:{caseId}:{start.ToLocalTime().ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture)}";

    public static string Jours(int mask)
    {
        if ((mask & 127) == 127) return "Tous les jours";
        if ((mask & 127) == 31) return "Du lundi au vendredi";
        if ((mask & 127) == 96) return "Le week-end";
        return string.Join(" ", Enumerable.Range(0, 7).Where(i => (mask & (1 << i)) != 0).Select(i => JourCourt[i]));
    }

    public static string When(DateTimeOffset t)
    {
        var local = t.ToLocalTime();
        var day = (local.Date - DateTime.Today).Days switch
        {
            0 => "Aujourd'hui",
            1 => "Demain",
            _ => $"{JourCourt[((int)local.DayOfWeek + 6) % 7]} {local:dd/MM}",
        };
        return $"{day} {local.ToString("HH:mm", CultureInfo.InvariantCulture)}";
    }

    /// <summary>Diffusions prévues par les cases entre deux instants, dans l'ordre.</summary>
    public static List<GrilleOccurrence> Occurrences(IEnumerable<GrilleCase> cases, DateTimeOffset from, DateTimeOffset to)
    {
        var list = new List<GrilleOccurrence>();
        var active = cases.Where(c => c.Active && (c.Jours & 127) != 0).ToList();
        for (var day = from.ToLocalTime().Date.AddDays(-1); day <= to.ToLocalTime().Date; day = day.AddDays(1))
        {
            foreach (var c in active)
            {
                if ((c.Jours & GrilleCase.Bit(day.DayOfWeek)) == 0) continue;
                var local = day + c.Heure;
                var start = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
                if (start > from && start <= to) list.Add(new GrilleOccurrence(c, start));
            }
        }
        return list.OrderBy(o => o.Start).ToList();
    }

    private sealed class Group(string key)
    {
        public string Key { get; } = key;
        public List<PlaylistEntry> Entries { get; } = new();
        public DateTimeOffset? Anchor => Entries.FirstOrDefault(e => e.StartMode == StartMode.HeureFixe)?.FixedStart;
        public DateTimeOffset? End => Anchor is { } a
            ? a + Entries.SkipWhile(e => e.StartMode != StartMode.HeureFixe).Aggregate(TimeSpan.Zero, (t, e) => t + e.PlayedDuration)
            : null;
    }

    /// <summary>
    /// Met le conducteur d'une chaîne à jour d'après sa grille.
    /// </summary>
    /// <param name="regenerate">Refaire aussi les cases à venir déjà programmées (sauf celle des 10 prochaines minutes).</param>
    public async Task<GrilleReport> FillAsync(int channelId, bool regenerate, CancellationToken ct = default)
    {
        await Lock.WaitAsync(ct);
        try
        {
            return await FillCoreAsync(channelId, regenerate, ct);
        }
        finally
        {
            Lock.Release();
        }
    }

    private async Task<GrilleReport> FillCoreAsync(int channelId, bool regenerate, CancellationToken ct)
    {
        var warnings = new List<string>();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var channel = await db.Channels.FirstOrDefaultAsync(c => c.Id == channelId, ct)
                      ?? throw new InvalidOperationException("Chaîne introuvable.");
        var cases = await db.GrilleCases.AsNoTracking().Include(c => c.ProgrammeType!).ThenInclude(t => t.Blocs)
            .Where(c => c.ChannelId == channelId).ToListAsync(ct);
        var entries = await db.PlaylistEntries.Include(e => e.Asset).Where(e => e.ChannelId == channelId)
            .OrderBy(e => e.Position).ToListAsync(ct);

        var now = DateTimeOffset.Now;
        var groups = entries.Where(e => e.GroupKey is not null).GroupBy(e => e.GroupKey!)
            .Select(g => { var group = new Group(g.Key); group.Entries.AddRange(g.OrderBy(e => e.Position)); return group; })
            .ToList();

        // 1. Ménage : ce qui est passé depuis plus de 30 min.
        var purged = 0;
        foreach (var g in groups.Where(g => g.End is { } end && end < now.AddMinutes(-30)).ToList())
        {
            purged += g.Entries.Count;
            db.PlaylistEntries.RemoveRange(g.Entries);
            entries.RemoveAll(g.Entries.Contains);
            groups.Remove(g);
        }

        // 2. Régénération : les cases à venir sont refaites (et les séries reprennent où elles en étaient).
        var removed = 0;
        if (regenerate)
        {
            var future = groups.Where(g => g.Anchor is { } a && a > now.AddMinutes(10)).OrderBy(g => g.Anchor).ToList();
            await RewindSeriesAsync(db, channelId, future.SelectMany(g => g.Entries).Select(e => e.Asset).OfType<MediaAsset>(), ct);
            foreach (var g in future)
            {
                removed++;
                db.PlaylistEntries.RemoveRange(g.Entries);
                entries.RemoveAll(g.Entries.Contains);
                groups.Remove(g);
            }
        }
        await db.SaveChangesAsync(ct);

        // 3. Nouvelles diffusions sur l'horizon.
        var horizon = now.ToLocalTime().Date.AddDays(Math.Clamp(channel.GrilleHorizonJours, 1, 7) + 1);
        var existing = groups.Select(g => g.Key).ToHashSet();
        var occurrences = Occurrences(cases, now.AddMinutes(1), new DateTimeOffset(horizon, TimeZoneInfo.Local.GetUtcOffset(horizon)))
            .Where(o => !existing.Contains(o.Key)).ToList();

        var spans = groups.Where(g => g.Anchor is not null).Select(g => (Start: g.Anchor!.Value, End: g.End!.Value)).ToList();
        var kinds = channel.RemplissageKinds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(k => Enum.TryParse<AssetKind>(k, out var kind) ? kind : (AssetKind?)null).OfType<AssetKind>().ToHashSet();
        var pool = kinds.Count == 0 ? new List<MediaAsset>()
            : (await db.MediaAssets.AsNoTracking().Where(a => a.Status == AssetStatus.Pret && kinds.Contains(a.Kind)).ToListAsync(ct))
              .Where(a => a.Duration > TimeSpan.Zero).ToList();

        var added = 0;
        foreach (var occ in occurrences)
        {
            var type = occ.Case.ProgrammeType;
            if (type is null) continue;
            var label = $"{When(occ.Start)} « {type.Name} »";

            var choices = new Dictionary<int, int>();
            var series = new Dictionary<int, string>();
            foreach (var b in type.Blocs)
            {
                if (b.Type == BlocType.AuChoix && occ.Case.AssetId is { } chosen) choices[b.Id] = chosen;
                if (b.Type == BlocType.EpisodeSuivant && string.IsNullOrWhiteSpace(b.SeriesName) && !string.IsNullOrWhiteSpace(occ.Case.SeriesName))
                    series[b.Id] = occ.Case.SeriesName!;
            }
            var expansion = await types.ExpandAsync(channelId, type.Id, choices, series, ct, hasardSiPasDeChoix: true);
            warnings.AddRange(expansion.Warnings.Select(w => $"{label} : {w}"));
            if (expansion.Items.Count == 0) continue;

            // Trou depuis la fin de la case précédente : comblé par du remplissage qui tient dedans.
            var previous = spans.Where(s => s.Start < occ.Start).OrderBy(s => s.Start).LastOrDefault();
            var filler = new List<MediaAsset>();
            if (previous != default)
            {
                if (previous.End > occ.Start)
                    warnings.Add($"{label} : la case précédente déborde de {Components.Shared.Fmt.Duration(previous.End - occ.Start)} (elle sera coupée).");
                var gap = occ.Start - (previous.End > now ? previous.End : now);
                if (gap > TimeSpan.FromSeconds(5))
                {
                    var left = gap;
                    // Tours successifs du stock (bandes-annonces d'abord) : un long trou (la nuit…) est rempli
                    // en repassant les médias, jamais deux fois de suite le même.
                    for (var round = 0; round < 50 && left >= TimeSpan.FromSeconds(5) && filler.Count < 400; round++)
                    {
                        var before = filler.Count;
                        var candidates = pool.OrderBy(a => a.Kind == AssetKind.BandeAnnonce ? 0 : 1).ThenBy(_ => Random.Shared.Next()).ToList();
                        foreach (var a in candidates)
                        {
                            if (a.Duration > left || (filler.Count > 0 && filler[^1].Id == a.Id)) continue;
                            filler.Add(a);
                            left -= a.Duration;
                            if (left < TimeSpan.FromSeconds(5) || filler.Count >= 400) break;
                        }
                        if (filler.Count == before) break;
                    }
                    if (left > TimeSpan.FromMinutes(1))
                        warnings.Add($"{label} : {Components.Shared.Fmt.Duration(left)} sans remplissage avant la case (bouche-trou).");
                }
            }

            var key = occ.Key;
            var batch = filler.Select(a => new PlaylistEntry { ChannelId = channelId, AssetId = a.Id, GroupKey = key, Remplissage = true }).ToList();
            batch.AddRange(expansion.Items.Select((item, n) => new PlaylistEntry
            {
                ChannelId = channelId,
                AssetId = item.AssetId,
                GroupKey = key,
                StartMode = n == 0 ? StartMode.HeureFixe : StartMode.Enchaine,
                FixedStart = n == 0 ? occ.Start : null,
            }));
            var position = entries.Count == 0 ? 0 : entries.Max(e => e.Position) + 1;
            foreach (var e in batch) e.Position = position++;
            db.PlaylistEntries.AddRange(batch);
            entries.AddRange(batch);
            await ProgrammeTypes.ApplySeriesAsync(db, channelId, expansion.SeriesUpdates, ct);
            await db.SaveChangesAsync(ct); // le tirage suivant voit cet épisode et ces médias
            spans.Add((occ.Start, occ.Start + expansion.Duration));
            added++;
        }

        // 4. Grille active : pas de boucle (les heures fixes sont datées).
        if (channel.GrilleActive && channel.Loop)
        {
            channel.Loop = false;
            await db.SaveChangesAsync(ct);
        }

        // Positions continues.
        var ordered = await db.PlaylistEntries.Where(e => e.ChannelId == channelId).OrderBy(e => e.Position).ToListAsync(ct);
        for (var i = 0; i < ordered.Count; i++) ordered[i].Position = i;
        await db.SaveChangesAsync(ct);

        // La même remarque pour chaque jour (jingle manquant…) n'est donnée qu'une fois.
        var merged = warnings
            .Select(w => (Full: w, Body: w.Contains(" : ") ? w[(w.IndexOf(" : ", StringComparison.Ordinal) + 3)..] : w))
            .GroupBy(w => w.Body)
            .Select(g => g.Count() == 1 ? g.First().Full : $"{g.First().Full} (×{g.Count()})")
            .ToList();
        var report = new GrilleReport(added, removed, purged, merged);
        if (added + removed + purged > 0)
            logger.LogInformation("Grille de « {Channel} » : {Summary}", channel.Name, report.Summary);
        return report;
    }

    /// <summary>Après suppression d'épisodes à venir, chaque série reprend juste avant le premier d'entre eux.</summary>
    private static async Task RewindSeriesAsync(NarDbContext db, int channelId, IEnumerable<MediaAsset> removed, CancellationToken ct)
    {
        foreach (var bySeries in removed.Where(a => a.SeriesName is not null).GroupBy(a => a.SeriesName!))
        {
            var episodes = await ProgrammeTypes.EpisodesAsync(db, bySeries.Key, ct);
            var first = bySeries.First();
            var index = episodes.FindIndex(e => e.Id == first.Id);
            if (index < 0) continue;
            int? previous = index > 0 ? episodes[index - 1].Id : null;
            await ProgrammeTypes.ApplySeriesAsync(db, channelId, new Dictionary<string, int?> { [bySeries.Key] = previous }, ct);
        }
    }
}

/// <summary>Toutes les 10 minutes, les chaînes à grille active sont programmées sur leur horizon.</summary>
public sealed class GrilleWorker(IDbContextFactory<NarDbContext> dbFactory, Grille grille, EngineSupervisor supervisor, ILogger<GrilleWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); }
        catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);
                var ids = await db.Channels.Where(c => c.GrilleActive).Select(c => c.Id).ToListAsync(stoppingToken);
                foreach (var id in ids)
                {
                    var report = await grille.FillAsync(id, regenerate: false, stoppingToken);
                    if (report.Added > 0) supervisor.Wake();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Programmation automatique de la grille impossible");
            }
            try { await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
