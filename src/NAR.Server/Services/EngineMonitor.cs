using System.Collections.Concurrent;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using NAR.Core.Status;
using NAR.Server.Data;

namespace NAR.Server.Services;

/// <summary>Lit l'état de chaque moteur toutes les secondes, pour la mosaïque et le conducteur.</summary>
public sealed class EngineMonitor(
    IDbContextFactory<NarDbContext> dbFactory,
    EngineConfigBuilder configBuilder,
    EngineSupervisor supervisor,
    IHttpClientFactory httpFactory,
    ILogger<EngineMonitor> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<int, (EngineStatusSnapshot Snapshot, DateTimeOffset At)> _status = new();
    private readonly ConcurrentDictionary<int, int> _ports = new();

    /// <summary>Déclenché après chaque tour de lecture (≈ 1 s).</summary>
    public event Action? Updated;

    public EngineStatusSnapshot? Get(int channelId) =>
        _status.TryGetValue(channelId, out var s) && DateTimeOffset.UtcNow - s.At < TimeSpan.FromSeconds(5) ? s.Snapshot : null;

    public int? PortOf(int channelId) => _ports.TryGetValue(channelId, out var p) ? p : null;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var http = httpFactory.CreateClient("engines");
        var lastPortRefresh = DateTimeOffset.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (DateTimeOffset.UtcNow - lastPortRefresh > TimeSpan.FromSeconds(5))
                {
                    lastPortRefresh = DateTimeOffset.UtcNow;
                    await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);
                    foreach (var c in await db.Channels.AsNoTracking().ToListAsync(stoppingToken))
                        _ports[c.Id] = configBuilder.EnginePort(c);
                }

                var tasks = _ports.Where(p => supervisor.Get(p.Key)?.Pid is not null)
                    .Select(async p =>
                    {
                        try
                        {
                            var snapshot = await http.GetFromJsonAsync<EngineStatusSnapshot>($"http://127.0.0.1:{p.Value}/status", stoppingToken);
                            if (snapshot is not null) _status[p.Key] = (snapshot, DateTimeOffset.UtcNow);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            // Moteur en démarrage : pas encore de statut.
                        }
                    });
                await Task.WhenAll(tasks);
                Updated?.Invoke();
                await Task.Delay(1000, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "Lecture de l'état des moteurs impossible");
                await Task.Delay(2000, stoppingToken);
            }
        }
    }
}
