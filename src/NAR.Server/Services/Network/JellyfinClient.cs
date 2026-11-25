using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NAR.Server.Services.Network;

public sealed record JellyfinLibrary(string Id, string Name, string? CollectionType);

public sealed record JellyfinItem(
    string Id,
    string Name,
    string Type,
    string? SeriesName,
    int? SeasonNumber,
    int? EpisodeNumber,
    TimeSpan Duration,
    int? Year,
    string? Overview,
    string? Rating,
    string[] Genres,
    long Size,
    DateTime ChangedUtc,
    string? Container);

public sealed class JellyfinException(string message) : Exception(message);

/// <summary>
/// Client de l'API Jellyfin (clé API créée dans Tableau de bord → Clés API).
/// La clé passe dans l'en-tête Authorization, jamais dans une adresse.
/// </summary>
public sealed class JellyfinClient
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _http;

    public JellyfinClient(HttpClient http, string baseUrl, string apiKey)
    {
        _http = http;
        BaseUrl = Normalize(baseUrl);
        ApiKey = apiKey;
    }

    public string BaseUrl { get; }
    public string ApiKey { get; }

    public static string Normalize(string url)
    {
        url = url.Trim().TrimEnd('/');
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            url = "http://" + url;
        if (url.EndsWith("/web", StringComparison.OrdinalIgnoreCase)) url = url[..^4];
        return url;
    }

    public HttpRequestMessage Request(HttpMethod method, string pathAndQuery)
    {
        var request = new HttpRequestMessage(method, BaseUrl + pathAndQuery);
        request.Headers.TryAddWithoutValidation("Authorization",
            $"MediaBrowser Client=\"NounoursAutoRegie\", Device=\"Regie\", DeviceId=\"nounoursautoregie\", Version=\"1.0\", Token=\"{ApiKey}\"");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    public async Task<string> ServerNameAsync(CancellationToken ct)
    {
        using var doc = await GetJsonAsync("/System/Info", ct);
        return doc.RootElement.TryGetProperty("ServerName", out var n) ? n.GetString() ?? "Jellyfin" : "Jellyfin";
    }

    public async Task<List<JellyfinLibrary>> LibrariesAsync(CancellationToken ct)
    {
        using var doc = await GetJsonAsync("/Library/VirtualFolders", ct);
        var list = new List<JellyfinLibrary>();
        foreach (var lib in doc.RootElement.EnumerateArray())
        {
            var id = lib.TryGetProperty("ItemId", out var i) ? i.GetString() : null;
            var name = lib.TryGetProperty("Name", out var n) ? n.GetString() : null;
            if (id is null || name is null) continue;
            var type = lib.TryGetProperty("CollectionType", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            list.Add(new JellyfinLibrary(id, name, type));
        }
        return list;
    }

    /// <summary>Films et épisodes d'une bibliothèque (par pages de 500).</summary>
    public async Task<List<JellyfinItem>> ItemsAsync(string libraryId, CancellationToken ct)
    {
        var all = new List<JellyfinItem>();
        for (var start = 0; ; start += 500)
        {
            using var doc = await GetJsonAsync(
                $"/Items?ParentId={Uri.EscapeDataString(libraryId)}&Recursive=true&IncludeItemTypes=Movie,Episode,MusicVideo,Video" +
                $"&Fields=Overview,Genres,DateCreated,MediaSources,OfficialRating,ProductionYear&IsMissing=false&StartIndex={start}&Limit=500", ct);
            var items = doc.RootElement.GetProperty("Items");
            foreach (var it in items.EnumerateArray())
            {
                var mediaSource = it.TryGetProperty("MediaSources", out var ms) && ms.ValueKind == JsonValueKind.Array && ms.GetArrayLength() > 0 ? ms[0] : (JsonElement?)null;
                if (it.TryGetProperty("LocationType", out var lt) && lt.GetString() == "Virtual") continue;
                all.Add(new JellyfinItem(
                    Id: it.GetProperty("Id").GetString()!,
                    Name: Str(it, "Name") ?? "Sans titre",
                    Type: Str(it, "Type") ?? "Video",
                    SeriesName: Str(it, "SeriesName"),
                    SeasonNumber: Int(it, "ParentIndexNumber"),
                    EpisodeNumber: Int(it, "IndexNumber"),
                    Duration: it.TryGetProperty("RunTimeTicks", out var rt) && rt.ValueKind == JsonValueKind.Number ? TimeSpan.FromTicks(rt.GetInt64()) : TimeSpan.Zero,
                    Year: Int(it, "ProductionYear"),
                    Overview: Str(it, "Overview"),
                    Rating: Str(it, "OfficialRating"),
                    Genres: it.TryGetProperty("Genres", out var g) && g.ValueKind == JsonValueKind.Array ? g.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray() : [],
                    Size: mediaSource is { } m && m.TryGetProperty("Size", out var sz) && sz.ValueKind == JsonValueKind.Number ? sz.GetInt64() : 0,
                    ChangedUtc: it.TryGetProperty("DateCreated", out var dc) && dc.TryGetDateTime(out var d) ? d.ToUniversalTime() : DateTime.MinValue,
                    Container: mediaSource is { } m2 ? Str(m2, "Container") : null));
            }
            var total = doc.RootElement.TryGetProperty("TotalRecordCount", out var tr) ? tr.GetInt32() : 0;
            if (start + 500 >= total || items.GetArrayLength() == 0) break;
        }
        return all;
    }

    /// <summary>Flux du fichier original, sans transcodage (lecture directe avec sauts).</summary>
    public string StreamPath(string itemId) =>
        $"/Videos/{Uri.EscapeDataString(itemId)}/stream?static=true&mediaSourceId={Uri.EscapeDataString(itemId)}";

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, path);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new JellyfinException($"Serveur Jellyfin injoignable ({BaseUrl}) : {ex.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new JellyfinException($"Le serveur Jellyfin ({BaseUrl}) ne répond pas.");
        }
        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new JellyfinException("Clé API refusée par Jellyfin. Créez-en une dans Tableau de bord → Clés API.");
            if (!response.IsSuccessStatusCode)
                throw new JellyfinException($"Jellyfin a répondu {(int)response.StatusCode} sur {path.Split('?')[0]}.");
            var stream = await response.Content.ReadAsStreamAsync(ct);
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
}
