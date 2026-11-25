using System.Net;
using NAR.Server.Services.Network;

namespace NAR.Server.Endpoints;

/// <summary>
/// Relais HTTP local vers les partages Windows : FFmpeg lit /interne/smb/{id}/… comme un fichier,
/// avec sauts (Range). Réservé à la machine elle-même (127.0.0.1).
/// </summary>
public static class SmbRelay
{
    public static void MapSmbRelay(this WebApplication app)
    {
        app.MapMethods("/interne/smb/{assetId:int}/{name}", ["GET", "HEAD"], async (int assetId, HttpContext http, SmbSources sources, ILoggerFactory logs) =>
        {
            var remote = http.Connection.RemoteIpAddress;
            if (remote is null || !IPAddress.IsLoopback(remote)) return Results.StatusCode(StatusCodes.Status403Forbidden);

            var found = await sources.ForAssetAsync(assetId, http.RequestAborted);
            if (found is not { } f) return Results.NotFound();

            SmbSession session;
            object handle;
            long size;
            try
            {
                session = await Task.Run(() => SmbSession.Open(f.Target), http.RequestAborted);
                try
                {
                    (handle, size) = session.OpenFile(SmbSources.RelativeInShare(f.Asset));
                }
                catch
                {
                    session.Dispose();
                    throw;
                }
            }
            catch (Exception ex) when (ex is SmbException or System.Net.Sockets.SocketException or IOException)
            {
                logs.CreateLogger("SmbRelay").LogWarning("Partage injoignable pour « {Title} » : {Error}", f.Asset.Title, ex.Message);
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            using (session)
            {
                try
                {
                    long start = 0, end = size - 1;
                    var partial = false;
                    var range = http.Request.Headers.Range.ToString();
                    if (range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) && !range.Contains(','))
                    {
                        var spec = range[6..].Split('-');
                        if (spec.Length == 2)
                        {
                            if (spec[0].Length == 0 && long.TryParse(spec[1], out var suffix)) start = Math.Max(0, size - suffix);
                            else if (long.TryParse(spec[0], out var s)) start = s;
                            if (spec[0].Length > 0 && spec[1].Length > 0 && long.TryParse(spec[1], out var e)) end = Math.Min(e, size - 1);
                            partial = true;
                        }
                        if (start >= size)
                        {
                            http.Response.Headers.ContentRange = $"bytes */{size}";
                            return Results.StatusCode(StatusCodes.Status416RangeNotSatisfiable);
                        }
                    }

                    http.Response.StatusCode = partial ? StatusCodes.Status206PartialContent : StatusCodes.Status200OK;
                    http.Response.Headers.AcceptRanges = "bytes";
                    http.Response.ContentType = "application/octet-stream";
                    http.Response.ContentLength = end - start + 1;
                    if (partial) http.Response.Headers.ContentRange = $"bytes {start}-{end}/{size}";
                    if (HttpMethods.IsHead(http.Request.Method)) return Results.Empty;

                    var offset = start;
                    var chunk = session.MaxReadSize;
                    while (offset <= end && !http.RequestAborted.IsCancellationRequested)
                    {
                        var want = (int)Math.Min(chunk, end - offset + 1);
                        var data = session.Read(handle, offset, want);
                        if (data.Length == 0) break;
                        await http.Response.Body.WriteAsync(data, http.RequestAborted);
                        offset += data.Length;
                    }
                }
                catch (OperationCanceledException) { /* FFmpeg a fermé la connexion (saut, fin) */ }
                catch (Exception ex) when (ex is SmbException or IOException or System.Net.Sockets.SocketException)
                {
                    logs.CreateLogger("SmbRelay").LogWarning("Lecture interrompue pour « {Title} » : {Error}", f.Asset.Title, ex.Message);
                }
                finally
                {
                    try { session.Close(handle); } catch { }
                }
                return Results.Empty;
            }
        }).AllowAnonymous();
    }

    /// <summary>Relais vers Jellyfin : le fichier original, avec sauts, la clé API restant côté serveur.</summary>
    public static void MapJellyfinRelay(this WebApplication app)
    {
        app.MapMethods("/interne/jellyfin/{assetId:int}/{name}", ["GET", "HEAD"], async (int assetId, HttpContext http, SmbSources sources, ILoggerFactory logs) =>
        {
            var remote = http.Connection.RemoteIpAddress;
            if (remote is null || !IPAddress.IsLoopback(remote)) return Results.StatusCode(StatusCodes.Status403Forbidden);

            var found = await sources.RemoteAssetAsync(assetId, Data.FolderSource.Jellyfin, http.RequestAborted);
            if (found is not { } f) return Results.NotFound();

            var client = sources.JellyfinOf(f.Folder);
            using var request = client.Request(HttpMethods.IsHead(http.Request.Method) ? HttpMethod.Head : HttpMethod.Get,
                client.StreamPath(SmbSources.JellyfinItemId(f.Asset)));
            request.Headers.Accept.Clear();
            if (http.Request.Headers.Range.Count > 0)
                request.Headers.TryAddWithoutValidation("Range", http.Request.Headers.Range.ToString());

            HttpResponseMessage response;
            try
            {
                response = await http.RequestServices.GetRequiredService<IHttpClientFactory>().CreateClient("jellyfin-stream")
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, http.RequestAborted);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !http.RequestAborted.IsCancellationRequested)
            {
                logs.CreateLogger("JellyfinRelay").LogWarning("Jellyfin injoignable pour « {Title} » : {Error}", f.Asset.Title, ex.Message);
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            using (response)
            {
                http.Response.StatusCode = (int)response.StatusCode;
                http.Response.Headers.AcceptRanges = "bytes";
                http.Response.ContentType = "application/octet-stream";
                if (response.Content.Headers.ContentLength is { } length) http.Response.ContentLength = length;
                if (response.Content.Headers.ContentRange is { } range) http.Response.Headers.ContentRange = range.ToString();
                if (!response.IsSuccessStatusCode || HttpMethods.IsHead(http.Request.Method)) return Results.Empty;
                try
                {
                    await response.Content.CopyToAsync(http.Response.Body, http.RequestAborted);
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or HttpRequestException) { }
                return Results.Empty;
            }
        }).AllowAnonymous();
    }
}
