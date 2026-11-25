using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using NAR.Core.Config;
using NAR.Server.Data;
using NAR.Server.Services;

namespace NAR.Server.Endpoints;

public static class Endpoints
{
    /// <summary>API réservée aux moteurs : configuration + conducteur de leur chaîne.</summary>
    public static void MapEngineApi(this WebApplication app)
    {
        app.MapGet("/api/engine/channels/{id:int}/config", async (int id, HttpContext http, EngineKey key, EngineConfigBuilder builder) =>
        {
            var given = http.Request.Headers["X-NAR-Engine-Key"].ToString();
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(key.Value)))
                return Results.StatusCode(StatusCodes.Status401Unauthorized);

            var config = await builder.BuildAsync(id, http.RequestAborted);
            if (config is null) return Results.NotFound();

            var etag = $"\"{config.Revision}\"";
            if (http.Request.Headers.IfNoneMatch.ToString() == etag) return Results.StatusCode(StatusCodes.Status304NotModified);
            http.Response.Headers.ETag = etag;
            return Results.Text(JsonSerializer.Serialize(config, ChannelConfig.JsonOptions), "application/json");
        }).AllowAnonymous();
    }

    /// <summary>
    /// Flux publics : /live/{id}/index.m3u8 (HLS relayé depuis le moteur) et /live/chaines.m3u (liste IPTV).
    /// Sans connexion, pour NounoursTV, Jellyfin et les lecteurs IPTV.
    /// </summary>
    public static void MapLive(this WebApplication app)
    {
        app.MapGet("/live/chaines.m3u", async (HttpContext http, IDbContextFactory<NarDbContext> dbFactory) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var channels = await db.Channels.AsNoTracking().Where(c => c.Enabled).OrderBy(c => c.Number).ToListAsync();
            var root = $"{http.Request.Scheme}://{http.Request.Host}";
            var sb = new StringBuilder($"#EXTM3U x-tvg-url=\"{root}/live/epg.xml\" url-tvg=\"{root}/live/epg.xml\"\n");
            foreach (var c in channels)
            {
                sb.Append(CultureInfo.InvariantCulture, $"#EXTINF:-1 tvg-id=\"nar-{c.Id}\" tvg-chno=\"{c.Number}\" group-title=\"NounoursCorp\",{c.Name}\n");
                sb.Append(CultureInfo.InvariantCulture, $"{root}/live/{c.Id}/index.m3u8\n");
            }
            http.Response.Headers.AccessControlAllowOrigin = "*";
            return Results.Text(sb.ToString(), "audio/x-mpegurl");
        }).AllowAnonymous();

        app.MapGet("/live/{id:int}/{**path}", async (int id, string path, HttpContext http, EngineMonitor monitor, IHttpClientFactory factory) =>
        {
            if (path.Contains("..") || path.Contains('\\')) return Results.BadRequest();
            if (monitor.PortOf(id) is not { } port) return Results.NotFound();

            var client = factory.CreateClient("live");
            HttpResponseMessage response;
            try
            {
                response = await client.GetAsync($"http://127.0.0.1:{port}/hls/{path}", HttpCompletionOption.ResponseHeadersRead, http.RequestAborted);
            }
            catch (HttpRequestException)
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            using (response)
            {
                http.Response.Headers.AccessControlAllowOrigin = "*";
                if (response.StatusCode != HttpStatusCode.OK) return Results.StatusCode((int)response.StatusCode);
                http.Response.StatusCode = 200;
                http.Response.ContentType = response.Content.Headers.ContentType?.ToString()
                                            ?? (path.EndsWith(".m3u8") ? "application/vnd.apple.mpegurl" : "video/mp2t");
                http.Response.Headers.CacheControl = path.EndsWith(".m3u8") ? "no-cache" : "max-age=60";
                await response.Content.CopyToAsync(http.Response.Body, http.RequestAborted);
                return Results.Empty;
            }
        }).AllowAnonymous();
    }

    /// <summary>Vignettes de la médiathèque (connexion requise).</summary>
    public static void MapMediaFiles(this WebApplication app)
    {
        app.MapGet("/medias/{id:int}/vignette.jpg", (int id, MediaLibrary library) =>
        {
            var path = library.ThumbnailPath(id);
            return File.Exists(path)
                ? Results.File(path, "image/jpeg", enableRangeProcessing: false, lastModified: File.GetLastWriteTimeUtc(path))
                : Results.NotFound();
        }).RequireAuthorization();
    }

    public static void MapAccountEndpoints(this WebApplication app)
    {
        app.MapPost("/deconnexion", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/connexion");
        });
    }
}
