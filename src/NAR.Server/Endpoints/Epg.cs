using System.Globalization;
using System.Text;
using System.Xml;
using Microsoft.EntityFrameworkCore;
using NAR.Server.Data;
using NAR.Server.Services;

namespace NAR.Server.Endpoints;

/// <summary>
/// Guide des programmes au format XMLTV (/live/epg.xml), public comme les flux :
/// NounoursTV, Jellyfin et les lecteurs IPTV l'associent à /live/chaines.m3u.
/// Les éléments courts d'habillage (jingles, pubs, bandes-annonces, bouche-trou) n'apparaissent pas :
/// le programme précédent est prolongé jusqu'au suivant, comme dans un vrai guide TV.
/// </summary>
public static class Epg
{
    private static readonly HashSet<AssetKind> Interstitials =
        [AssetKind.Jingle, AssetKind.Spot, AssetKind.BandeAnnonce, AssetKind.Habillage, AssetKind.BoucheTrou];

    public static void MapEpg(this WebApplication app)
    {
        app.MapGet("/live/epg.xml", async (HttpContext http, IDbContextFactory<NarDbContext> dbFactory, ScheduleService schedule, int? heures) =>
        {
            var horizon = TimeSpan.FromHours(Math.Clamp(heures ?? 48, 1, 168));
            await using var db = await dbFactory.CreateDbContextAsync();
            var channels = await db.Channels.AsNoTracking().Where(c => c.Enabled).OrderBy(c => c.Number).ToListAsync();

            var settings = new XmlWriterSettings { Async = true, Indent = true, Encoding = new UTF8Encoding(false) };
            http.Response.ContentType = "application/xml; charset=utf-8";
            http.Response.Headers.AccessControlAllowOrigin = "*";
            http.Response.Headers.CacheControl = "max-age=60";

            // Écrit en mémoire puis envoie d'un bloc (XmlWriter écrit de façon synchrone).
            using var buffer = new MemoryStream();
            await using (var xml = XmlWriter.Create(buffer, settings))
            {
            await xml.WriteStartDocumentAsync();
            xml.WriteDocType("tv", null, "xmltv.dtd", null);
            xml.WriteStartElement("tv");
            xml.WriteAttributeString("generator-info-name", "NounoursAutoRegie");

            foreach (var c in channels)
            {
                xml.WriteStartElement("channel");
                xml.WriteAttributeString("id", ChannelId(c));
                xml.WriteElementString("display-name", c.Name);
                xml.WriteElementString("lcn", c.Number.ToString(CultureInfo.InvariantCulture));
                xml.WriteEndElement();
            }

            foreach (var c in channels)
            {
                var upcoming = await schedule.UpcomingAsync(c.Id, horizon, http.RequestAborted);
                if (upcoming is not { } u) continue;
                var programmes = Merge(u.Items);
                foreach (var (p, end) in programmes)
                {
                    var asset = p.Entry.Asset!;
                    xml.WriteStartElement("programme");
                    xml.WriteAttributeString("start", XmlTime(p.Start));
                    xml.WriteAttributeString("stop", XmlTime(end));
                    xml.WriteAttributeString("channel", ChannelId(c));

                    var isEpisode = asset.SeriesName is not null && string.IsNullOrWhiteSpace(p.Entry.TitleOverride);
                    var title = isEpisode ? asset.SeriesName! : p.Entry.DisplayTitle;
                    if (asset.Year is { } y && title.EndsWith($" ({y})")) title = title[..^($" ({y})".Length)];
                    WriteLang(xml, "title", title);
                    if (isEpisode && asset.EpisodeTitle is { Length: > 0 } sub) WriteLang(xml, "sub-title", sub);
                    if (asset.Overview is { Length: > 0 } desc) WriteLang(xml, "desc", desc);
                    if (asset.Year is { } year) xml.WriteElementString("date", year.ToString(CultureInfo.InvariantCulture));
                    WriteLang(xml, "category", Components.Shared.Fmt.Kind(asset.Kind));
                    foreach (var genre in (asset.Genres ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                        WriteLang(xml, "category", genre);
                    if (asset.SeasonNumber is not null || asset.EpisodeNumber is not null)
                    {
                        xml.WriteStartElement("episode-num");
                        xml.WriteAttributeString("system", "xmltv_ns");
                        xml.WriteString($"{(asset.SeasonNumber is { } s ? s - 1 : 0)}.{(asset.EpisodeNumber is { } e ? e - 1 : 0)}.");
                        xml.WriteEndElement();
                        xml.WriteStartElement("episode-num");
                        xml.WriteAttributeString("system", "onscreen");
                        xml.WriteString($"S{asset.SeasonNumber ?? 0:00}E{asset.EpisodeNumber ?? 0:00}");
                        xml.WriteEndElement();
                    }
                    if (asset.Rating is { Length: > 0 } rating)
                    {
                        xml.WriteStartElement("rating");
                        xml.WriteElementString("value", rating);
                        xml.WriteEndElement();
                    }
                    xml.WriteEndElement();
                }
            }
            xml.WriteEndElement();
            await xml.WriteEndDocumentAsync();
            await xml.FlushAsync();
            }
            http.Response.ContentLength = buffer.Length;
            buffer.Position = 0;
            await buffer.CopyToAsync(http.Response.Body, http.RequestAborted);
        }).AllowAnonymous();
    }

    /// <summary>Retire l'habillage et prolonge chaque programme jusqu'au suivant.</summary>
    private static List<(UpcomingProgramme Programme, DateTimeOffset End)> Merge(List<UpcomingProgramme> items)
    {
        var shows = items.Where(p => !Interstitials.Contains(p.Entry.Asset!.Kind)).ToList();
        // Une chaîne qui ne passe que de l'habillage : on affiche tout plutôt qu'un guide vide.
        if (shows.Count == 0) shows = items;
        var merged = new List<(UpcomingProgramme, DateTimeOffset)>();
        for (var i = 0; i < shows.Count; i++)
        {
            var end = i + 1 < shows.Count && shows[i + 1].Start > shows[i].End && shows[i + 1].Start - shows[i].End < TimeSpan.FromMinutes(30)
                ? shows[i + 1].Start
                : shows[i].End;
            merged.Add((shows[i], end));
        }
        return merged;
    }

    public static string ChannelId(Channel c) => $"nar-{c.Id}";

    private static string XmlTime(DateTimeOffset t) =>
        t.ToString("yyyyMMddHHmmss ", CultureInfo.InvariantCulture) + t.ToString("zzz", CultureInfo.InvariantCulture).Replace(":", "");

    private static void WriteLang(XmlWriter xml, string name, string value)
    {
        xml.WriteStartElement(name);
        xml.WriteAttributeString("lang", "fr");
        xml.WriteString(value);
        xml.WriteEndElement();
    }
}
