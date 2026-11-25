using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NAR.Server.Data;

namespace NAR.Server.Services;

/// <summary>Un élément prévu par un programme type, avant insertion dans le conducteur.</summary>
public sealed record PlannedItem(int AssetId, string Title, AssetKind Kind, TimeSpan Duration, string Bloc);

/// <summary>Résultat du dépliage d'un programme type : les éléments, les remarques, et l'avancée des séries.</summary>
public sealed record Expansion(int TypeId, string TypeName, List<PlannedItem> Items, List<string> Warnings, Dictionary<string, int> SeriesUpdates)
{
    public TimeSpan Duration => Items.Aggregate(TimeSpan.Zero, (t, i) => t + i.Duration);
}

/// <summary>
/// Programmes types : un modèle (« Film du soir » = écran pub → jingle du film → film) est déplié
/// en éléments concrets au moment de l'insertion. Le conducteur reste une liste de médias ordinaire :
/// le moteur, l'EPG et le journal n'ont rien de spécial à savoir.
/// </summary>
public sealed partial class ProgrammeTypes(IDbContextFactory<NarDbContext> dbFactory)
{
    private static readonly AssetKind[] ProgrammeKinds = [AssetKind.Programme, AssetKind.Film, AssetKind.Episode];

    public static string Describe(ProgrammeTypeBloc b) => b.Type switch
    {
        BlocType.Media => b.Asset?.Title ?? "média supprimé",
        BlocType.AuHasard => $"{Kind(b.Kind)} au hasard",
        BlocType.AuChoix => $"{Kind(b.Kind)} choisi à l'insertion",
        BlocType.EpisodeSuivant => (b.Count > 1 ? $"{b.Count} épisodes suivants" : "Épisode suivant") + (string.IsNullOrWhiteSpace(b.SeriesName) ? " (série choisie à l'insertion)" : $" de « {b.SeriesName} »"),
        BlocType.EcranPub => $"Écran pub de {Components.Shared.Fmt.Duration(TimeSpan.FromSeconds(b.DurationSeconds ?? 120))}",
        BlocType.HabillageDuProgramme => $"{AtelierLabel(b.AtelierKind)} Rétro 95 du programme",
        _ => b.Type.ToString(),
    };

    public static string TypeLabel(BlocType t) => t switch
    {
        BlocType.Media => "Média fixe",
        BlocType.AuHasard => "Au hasard",
        BlocType.AuChoix => "Au choix (à l'insertion)",
        BlocType.EpisodeSuivant => "Épisode suivant d'une série",
        BlocType.EcranPub => "Écran pub",
        BlocType.HabillageDuProgramme => "Habillage du programme (atelier)",
        _ => t.ToString(),
    };

    public static string AtelierLabel(int? kind) => kind switch
    {
        2 => "Générique",
        3 => "Bande-annonce",
        _ => "Jingle",
    };

    private static string Kind(AssetKind? k) => k is { } kind ? Components.Shared.Fmt.Kind(kind) : "Média";

    /// <summary>Séries connues de la médiathèque (pour « épisode suivant »).</summary>
    public async Task<List<string>> SeriesAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.MediaAssets.AsNoTracking()
            .Where(a => a.SeriesName != null && a.Status == AssetStatus.Pret)
            .Select(a => a.SeriesName!).Distinct().OrderBy(s => s).ToListAsync(ct);
    }

    /// <summary>Où en est la chaîne dans une série (dernier épisode programmé).</summary>
    public async Task<string?> SeriesPositionAsync(int channelId, string series, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var p = await db.SeriesProgress.AsNoTracking().FirstOrDefaultAsync(x => x.ChannelId == channelId && x.SeriesName == series, ct);
        if (p?.LastAssetId is not { } id) return null;
        return (await db.MediaAssets.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct))?.Title;
    }

    /// <summary>Déplie un programme type pour une chaîne, sans rien enregistrer (aperçu).</summary>
    /// <param name="choices">Média choisi pour chaque bloc « au choix » (id du bloc → id du média).</param>
    /// <param name="seriesChoices">Série choisie pour chaque bloc « épisode suivant » sans série fixée.</param>
    /// <param name="hasardSiPasDeChoix">Grille : un bloc « au choix » sans choix est tiré au sort dans son type.</param>
    public async Task<Expansion> ExpandAsync(int channelId, int typeId, IReadOnlyDictionary<int, int> choices,
        IReadOnlyDictionary<int, string> seriesChoices, CancellationToken ct = default, bool hasardSiPasDeChoix = false)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var type = await db.ProgrammeTypes.AsNoTracking()
            .Include(t => t.Blocs.OrderBy(b => b.Position)).ThenInclude(b => b.Asset)
            .FirstOrDefaultAsync(t => t.Id == typeId, ct)
            ?? throw new InvalidOperationException("Programme type introuvable.");

        var warnings = new List<string>();
        var series = new Dictionary<string, int>();
        var blocs = type.Blocs.OrderBy(b => b.Position).ToList();
        var resolved = new List<PlannedItem>?[blocs.Count];
        var onAir = (await db.PlaylistEntries.AsNoTracking().Where(e => e.ChannelId == channelId).Select(e => e.AssetId).ToListAsync(ct)).ToHashSet();

        static PlannedItem Item(MediaAsset a, string bloc) => new(a.Id, a.Title, a.Kind, a.Duration, bloc);

        // 1. Tout sauf l'habillage, qui dépend du programme.
        for (var i = 0; i < blocs.Count; i++)
        {
            var b = blocs[i];
            var label = string.IsNullOrWhiteSpace(b.Label) ? TypeLabel(b.Type) : b.Label!;
            switch (b.Type)
            {
                case BlocType.Media:
                    if (b.Asset is { Status: AssetStatus.Pret } fixedAsset) resolved[i] = [Item(fixedAsset, label)];
                    else warnings.Add($"« {label} » : le média de ce bloc n'est plus disponible.");
                    break;

                case BlocType.AuChoix when hasardSiPasDeChoix && !choices.ContainsKey(b.Id):
                case BlocType.AuHasard:
                {
                    var pool = await db.MediaAssets.AsNoTracking()
                        .Where(a => a.Status == AssetStatus.Pret && (b.Kind == null || a.Kind == b.Kind))
                        .ToListAsync(ct);
                    pool.RemoveAll(a => a.Duration <= TimeSpan.Zero);
                    // De préférence un média qui n'est pas déjà au conducteur.
                    var fresh = pool.Where(a => !onAir.Contains(a.Id)).ToList();
                    var pick = (fresh.Count > 0 ? fresh : pool).OrderBy(_ => Random.Shared.Next()).FirstOrDefault();
                    if (pick is null) warnings.Add($"« {label} » : aucun média de type {Kind(b.Kind)} dans la médiathèque.");
                    else resolved[i] = [Item(pick, label)];
                    break;
                }

                case BlocType.AuChoix:
                {
                    var asset = choices.TryGetValue(b.Id, out var id)
                        ? await db.MediaAssets.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id && a.Status == AssetStatus.Pret, ct)
                        : null;
                    if (asset is null) warnings.Add($"« {label} » : choisissez le média.");
                    else resolved[i] = [Item(asset, label)];
                    break;
                }

                case BlocType.EpisodeSuivant:
                {
                    var name = !string.IsNullOrWhiteSpace(b.SeriesName) ? b.SeriesName!
                        : seriesChoices.TryGetValue(b.Id, out var chosen) ? chosen : null;
                    if (string.IsNullOrWhiteSpace(name)) { warnings.Add($"« {label} » : choisissez la série."); break; }
                    var episodes = (await db.MediaAssets.AsNoTracking()
                            .Where(a => a.SeriesName == name && a.Status == AssetStatus.Pret)
                            .ToListAsync(ct))
                        .Where(a => a.Duration > TimeSpan.Zero)
                        .OrderBy(a => a.SeasonNumber ?? int.MaxValue).ThenBy(a => a.EpisodeNumber ?? int.MaxValue).ThenBy(a => a.Title, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (episodes.Count == 0) { warnings.Add($"« {label} » : aucun épisode de « {name} » dans la médiathèque."); break; }
                    int? last = series.TryGetValue(name, out var inThisType) ? inThisType
                        : (await db.SeriesProgress.AsNoTracking().FirstOrDefaultAsync(p => p.ChannelId == channelId && p.SeriesName == name, ct))?.LastAssetId;
                    var index = last is { } l ? episodes.FindIndex(e => e.Id == l) + 1 : 0;
                    var list = new List<PlannedItem>();
                    for (var n = 0; n < Math.Clamp(b.Count, 1, 20); n++)
                    {
                        if (index >= episodes.Count)
                        {
                            index = 0;
                            warnings.Add($"« {name} » : dernier épisode atteint, la série reprend au premier.");
                        }
                        list.Add(Item(episodes[index], label));
                        series[name] = episodes[index].Id;
                        index++;
                    }
                    resolved[i] = list;
                    break;
                }

                case BlocType.EcranPub:
                {
                    var target = TimeSpan.FromSeconds(Math.Clamp(b.DurationSeconds ?? 120, 10, 1800));
                    var spots = (await db.MediaAssets.AsNoTracking()
                            .Where(a => a.Status == AssetStatus.Pret && a.Kind == AssetKind.Spot)
                            .ToListAsync(ct))
                        .Where(a => a.Duration > TimeSpan.Zero)
                        .OrderBy(_ => Random.Shared.Next()).ToList();
                    if (spots.Count == 0) { warnings.Add($"« {label} » : aucun spot pub dans la médiathèque."); break; }
                    var list = new List<PlannedItem>();
                    var total = TimeSpan.Zero;
                    foreach (var s in spots)
                    {
                        if (total + s.Duration > target * 1.1) continue;
                        list.Add(Item(s, label));
                        total += s.Duration;
                        if (total >= target * 0.9) break;
                    }
                    if (list.Count == 0) list.Add(Item(spots.MinBy(s => s.Duration)!, label));
                    resolved[i] = list;
                    break;
                }
            }
        }

        // 2. Habillage : la création de l'atelier qui correspond au programme du modèle.
        var programme = resolved.Where(r => r is not null).SelectMany(r => r!).FirstOrDefault(p => ProgrammeKinds.Contains(p.Kind));
        MediaAsset? programmeAsset = programme is null ? null : await db.MediaAssets.AsNoTracking().FirstOrDefaultAsync(a => a.Id == programme.AssetId, ct);
        for (var i = 0; i < blocs.Count; i++)
        {
            var b = blocs[i];
            if (b.Type != BlocType.HabillageDuProgramme) continue;
            var what = AtelierLabel(b.AtelierKind);
            var label = string.IsNullOrWhiteSpace(b.Label) ? what : b.Label!;
            if (programmeAsset is null) { warnings.Add($"« {label} » : pas de programme dans ce modèle pour trouver son habillage."); continue; }
            var creation = await FindCreationAsync(db, programmeAsset, b.AtelierKind ?? 0, ct);
            if (creation is null)
                warnings.Add($"Pas de {what.ToLowerInvariant()} Rétro 95 pour « {programmeAsset.SeriesName ?? programmeAsset.Title} » : fabrique-le dans l'atelier (titre du programme, ou programme lié), il sera repris automatiquement.");
            else
                resolved[i] = [Item(creation, label)];
        }

        var items = resolved.Where(r => r is not null).SelectMany(r => r!).ToList();
        return new Expansion(type.Id, type.Name, items, warnings, series);
    }

    /// <summary>Insère un programme type déplié dans le conducteur et fait avancer les séries.</summary>
    /// <returns>Identifiant du premier élément inséré.</returns>
    public async Task<int?> InsertAsync(int channelId, Expansion expansion, int insertAt, DateTimeOffset? fixedStart, CancellationToken ct = default)
    {
        if (expansion.Items.Count == 0) return null;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entries = await db.PlaylistEntries.Where(e => e.ChannelId == channelId).OrderBy(e => e.Position).ToListAsync(ct);
        var added = expansion.Items.Select((item, n) => new PlaylistEntry
        {
            ChannelId = channelId,
            AssetId = item.AssetId,
            StartMode = n == 0 && fixedStart is not null ? StartMode.HeureFixe : StartMode.Enchaine,
            FixedStart = n == 0 ? fixedStart : null,
        }).ToList();
        entries.InsertRange(Math.Clamp(insertAt, 0, entries.Count), added);
        db.PlaylistEntries.AddRange(added);
        for (var i = 0; i < entries.Count; i++) entries[i].Position = i;

        await ApplySeriesAsync(db, channelId, expansion.SeriesUpdates, ct);
        await db.SaveChangesAsync(ct);
        return added[0].Id;
    }

    /// <summary>Enregistre le dernier épisode programmé de chaque série (sans SaveChanges).</summary>
    public static async Task ApplySeriesAsync(NarDbContext db, int channelId, IReadOnlyDictionary<string, int?> updates, CancellationToken ct)
    {
        foreach (var (name, assetId) in updates)
        {
            var progress = await db.SeriesProgress.FirstOrDefaultAsync(p => p.ChannelId == channelId && p.SeriesName == name, ct);
            if (progress is null) db.SeriesProgress.Add(progress = new SeriesProgress { ChannelId = channelId, SeriesName = name });
            progress.LastAssetId = assetId;
            progress.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    public static Task ApplySeriesAsync(NarDbContext db, int channelId, Dictionary<string, int> updates, CancellationToken ct) =>
        ApplySeriesAsync(db, channelId, updates.ToDictionary(kv => kv.Key, kv => (int?)kv.Value), ct);

    /// <summary>Épisodes d'une série dans l'ordre de diffusion.</summary>
    public static async Task<List<MediaAsset>> EpisodesAsync(NarDbContext db, string series, CancellationToken ct) =>
        (await db.MediaAssets.AsNoTracking().Where(a => a.SeriesName == series && a.Status == AssetStatus.Pret).ToListAsync(ct))
        .Where(a => a.Duration > TimeSpan.Zero)
        .OrderBy(a => a.SeasonNumber ?? int.MaxValue).ThenBy(a => a.EpisodeNumber ?? int.MaxValue).ThenBy(a => a.Title, StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static async Task<MediaAsset?> FindCreationAsync(NarDbContext db, MediaAsset programme, int atelierKind, CancellationToken ct)
    {
        var creations = (await db.AtelierCreations.AsNoTracking()
                .Where(c => c.Kind == atelierKind && c.Status == CreationStatus.Pret && c.AssetId != null)
                .ToListAsync(ct))
            .OrderByDescending(c => c.FinishedAt ?? c.CreatedAt)
            .ToList();
        var names = new[] { programme.Title, programme.SeriesName, StripYear(programme.Title) }
            .Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => Normalize(n!)).Where(n => n.Length > 0).ToHashSet();

        foreach (var c in creations)
        {
            var linked = c.SourceAssetId == programme.Id;
            var title = Normalize(c.Title);
            var sameName = title.Length > 0 && (names.Contains(title) || names.Any(n => title.Length >= 4 && n.StartsWith(title, StringComparison.Ordinal)));
            if (!linked && !sameName) continue;
            var asset = await db.MediaAssets.AsNoTracking().FirstOrDefaultAsync(a => a.Id == c.AssetId && a.Status == AssetStatus.Pret, ct);
            if (asset is not null) return asset;
        }
        return null;
    }

    private static string StripYear(string title) => YearSuffix().Replace(title, "").Trim();

    /// <summary>« Le Grand Film ! » et « LE GRAND FILM » se ressemblent : minuscules, sans accents ni ponctuation.</summary>
    public static string Normalize(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"\s*\(\d{4}\)\s*$")]
    private static partial Regex YearSuffix();

    /// <summary>Modèles de départ, créés une fois quand il n'y en a aucun.</summary>
    public async Task SeedAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.ProgrammeTypes.AnyAsync(ct)) return;
        db.ProgrammeTypes.AddRange(
            new ProgrammeType
            {
                Name = "Film du soir",
                Description = "Écran pub, jingle Rétro 95 du film, le film.",
                Blocs =
                [
                    new() { Position = 0, Type = BlocType.EcranPub, Label = "Pub", DurationSeconds = 120 },
                    new() { Position = 1, Type = BlocType.HabillageDuProgramme, Label = "Jingle", AtelierKind = 0 },
                    new() { Position = 2, Type = BlocType.AuChoix, Label = "Film", Kind = AssetKind.Film },
                ],
            },
            new ProgrammeType
            {
                Name = "Épisode de série",
                Description = "Jingle Rétro 95 de la série puis l'épisode suivant.",
                Blocs =
                [
                    new() { Position = 0, Type = BlocType.HabillageDuProgramme, Label = "Jingle", AtelierKind = 0 },
                    new() { Position = 1, Type = BlocType.EpisodeSuivant, Label = "Épisode", Count = 1 },
                ],
            });
        await db.SaveChangesAsync(ct);
    }
}
