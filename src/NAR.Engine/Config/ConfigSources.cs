using System.Net;
using System.Net.Http.Headers;
using NAR.Core.Config;

namespace NAR.Engine.Config;

/// <summary>
/// D'où vient la configuration de la chaîne : un fichier JSON (mode autonome)
/// ou NAR.Server (mode régie). Elle est relue en continu pour les modifications à chaud.
/// </summary>
public interface IChannelConfigSource
{
    string Description { get; }
    Task<ChannelConfig> LoadInitialAsync(CancellationToken ct);

    /// <summary>Retourne la nouvelle configuration si elle a changé depuis le dernier appel, sinon null.</summary>
    Task<ChannelConfig?> PollAsync(CancellationToken ct);
}

/// <summary>Mode autonome : un fichier channel.json, relu dès qu'il est modifié.</summary>
public sealed class FileConfigSource(string path, ILogger<FileConfigSource> logger) : IChannelConfigSource
{
    private readonly string _path = Path.GetFullPath(path);
    private DateTime _lastWrite;

    public string Description => _path;

    public Task<ChannelConfig> LoadInitialAsync(CancellationToken ct)
    {
        _lastWrite = File.GetLastWriteTimeUtc(_path);
        return Task.FromResult(ChannelConfig.Load(_path));
    }

    public async Task<ChannelConfig?> PollAsync(CancellationToken ct)
    {
        var write = File.GetLastWriteTimeUtc(_path);
        if (write == _lastWrite) return null;

        // Laisse l'éditeur finir d'écrire le fichier.
        await Task.Delay(300, ct);
        _lastWrite = File.GetLastWriteTimeUtc(_path);
        try
        {
            var config = ChannelConfig.Load(_path);
            logger.LogInformation("Configuration modifiée : {Path}", _path);
            return config;
        }
        catch (Exception ex)
        {
            logger.LogError("Configuration illisible, on garde la précédente : {Error}", ex.Message);
            return null;
        }
    }
}

/// <summary>Mode régie : la configuration et le conducteur viennent de NAR.Server.</summary>
public sealed class RemoteConfigSource : IChannelConfigSource
{
    public const string KeyHeader = "X-NAR-Engine-Key";

    private readonly HttpClient _http;
    private readonly string _url;
    private readonly ILogger _logger;
    private long _revision = -1;

    public RemoteConfigSource(string serverUrl, string channelId, string? key, ILogger<RemoteConfigSource> logger)
    {
        _url = $"{serverUrl.TrimEnd('/')}/api/engine/channels/{Uri.EscapeDataString(channelId)}/config";
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        if (!string.IsNullOrEmpty(key)) _http.DefaultRequestHeaders.Add(KeyHeader, key);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public string Description => _url;

    public async Task<ChannelConfig> LoadInitialAsync(CancellationToken ct)
    {
        while (true)
        {
            try
            {
                var config = await FetchAsync(ct) ?? throw new InvalidOperationException("Réponse vide");
                return config;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogWarning("Serveur injoignable ({Error}), nouvel essai dans 3 s", ex.Message);
                await Task.Delay(3000, ct);
            }
        }
    }

    public async Task<ChannelConfig?> PollAsync(CancellationToken ct)
    {
        try
        {
            return await FetchAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Le serveur peut redémarrer : l'antenne continue avec le dernier conducteur connu.
            _logger.LogDebug("Serveur injoignable : {Error}", ex.Message);
            return null;
        }
    }

    private async Task<ChannelConfig?> FetchAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _url);
        if (_revision >= 0) request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue($"\"{_revision}\""));
        using var response = await _http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotModified) return null;
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var config = ChannelConfig.Parse(json, AppContext.BaseDirectory);
        if (config.Revision == _revision) return null;
        _revision = config.Revision;
        return config;
    }
}
