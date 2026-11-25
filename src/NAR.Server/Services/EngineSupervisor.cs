using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NAR.Server.Data;

namespace NAR.Server.Services;

public enum EngineState
{
    Arrete,
    Demarrage,
    EnCours,
    Redemarrage,
    Erreur,
}

/// <summary>Un moteur de chaîne vu par le superviseur.</summary>
public sealed class EngineProcess
{
    private readonly ConcurrentQueue<string> _log = new();
    internal Process? Process;
    internal bool Wanted;
    internal DateTimeOffset NextStartAt;
    internal int QuickCrashes;

    public required int ChannelId { get; init; }
    public EngineState State { get; internal set; } = EngineState.Arrete;
    public DateTimeOffset? StartedAt { get; internal set; }
    public int Restarts { get; internal set; }
    public int? LastExitCode { get; internal set; }
    public int? Pid => Process is { HasExited: false } p ? p.Id : null;
    public IReadOnlyCollection<string> Log => _log.ToArray();

    internal void AddLog(string line)
    {
        _log.Enqueue($"{DateTime.Now:HH:mm:ss} {line}");
        while (_log.Count > 300 && _log.TryDequeue(out _)) { }
    }
}

/// <summary>
/// Fait tourner un moteur (processus séparé) par chaîne allumée, et le relance s'il s'arrête :
/// un plantage ne touche qu'une chaîne, et l'antenne repart seule en quelques secondes.
/// </summary>
public sealed class EngineSupervisor(
    IDbContextFactory<NarDbContext> dbFactory,
    IOptions<NarOptions> options,
    EngineKey engineKey,
    ILogger<EngineSupervisor> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<int, EngineProcess> _engines = new();
    private readonly SemaphoreSlim _wake = new(0);

    public event Action? Changed;

    public EngineProcess? Get(int channelId) => _engines.TryGetValue(channelId, out var e) ? e : null;

    /// <summary>Demande une réconciliation immédiate (chaîne allumée / éteinte depuis la régie).</summary>
    public void Wake() => _wake.Release();

    public void Restart(int channelId)
    {
        if (_engines.TryGetValue(channelId, out var engine))
        {
            engine.AddLog("Redémarrage demandé depuis la régie");
            Kill(engine);
            engine.NextStartAt = DateTimeOffset.UtcNow;
            engine.QuickCrashes = 0;
        }
        Wake();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var enginePath = ResolveEngine();
        logger.LogInformation("Moteur utilisé : {Engine}", enginePath is null ? "INTROUVABLE" : string.Join(' ', enginePath));

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await ReconcileAsync(enginePath, stoppingToken);
                await _wake.WaitAsync(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            foreach (var engine in _engines.Values) Kill(engine);
        }
    }

    private async Task ReconcileAsync(string[]? enginePath, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var channels = await db.Channels.AsNoTracking().Select(c => new { c.Id, c.Name, c.Enabled }).ToListAsync(ct);
        var changed = false;

        foreach (var channel in channels)
        {
            var engine = _engines.GetOrAdd(channel.Id, id => new EngineProcess { ChannelId = id });
            engine.Wanted = channel.Enabled;
            var running = engine.Process is { HasExited: false };

            if (!engine.Wanted && running)
            {
                engine.AddLog("Antenne éteinte depuis la régie");
                Kill(engine);
                engine.State = EngineState.Arrete;
                changed = true;
            }
            else if (engine.Wanted && !running && DateTimeOffset.UtcNow >= engine.NextStartAt)
            {
                if (enginePath is null)
                {
                    if (engine.State != EngineState.Erreur) engine.AddLog("Exécutable NAR.Engine introuvable (réglage Nar:EnginePath)");
                    engine.State = EngineState.Erreur;
                }
                else
                {
                    Start(engine, channel.Name, enginePath);
                }
                changed = true;
            }
            else if (!engine.Wanted && engine.State != EngineState.Arrete)
            {
                engine.State = EngineState.Arrete;
                changed = true;
            }
        }

        // Chaînes supprimées.
        foreach (var id in _engines.Keys.Except(channels.Select(c => c.Id)).ToList())
        {
            if (_engines.TryRemove(id, out var gone)) Kill(gone);
            changed = true;
        }
        if (changed) Changed?.Invoke();
    }

    private void Start(EngineProcess engine, string name, string[] enginePath)
    {
        var o = options.Value;
        var psi = new ProcessStartInfo(enginePath[0])
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = o.ChannelDirectory(engine.ChannelId),
        };
        foreach (var a in enginePath.Skip(1)) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("--server");
        psi.ArgumentList.Add($"http://127.0.0.1:{o.HttpPort}");
        psi.ArgumentList.Add("--channel");
        psi.ArgumentList.Add(engine.ChannelId.ToString());
        psi.Environment["NAR_ENGINE_KEY"] = engineKey.Value;
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        Directory.CreateDirectory(psi.WorkingDirectory);

        try
        {
            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) engine.AddLog(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) engine.AddLog(e.Data); };
            process.Exited += (_, _) => OnExited(engine, process, name);
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            engine.Process = process;
            engine.StartedAt = DateTimeOffset.UtcNow;
            engine.State = engine.Restarts > 0 ? EngineState.Redemarrage : EngineState.Demarrage;
            engine.AddLog($"Moteur lancé (pid {process.Id})");
            logger.LogInformation("Chaîne « {Name} » : moteur lancé (pid {Pid})", name, process.Id);
            _ = Task.Delay(5000).ContinueWith(_ =>
            {
                if (ReferenceEquals(engine.Process, process) && !process.HasExited)
                {
                    engine.State = EngineState.EnCours;
                    Changed?.Invoke();
                }
            });
        }
        catch (Exception ex)
        {
            engine.State = EngineState.Erreur;
            engine.AddLog($"Lancement impossible : {ex.Message}");
            engine.NextStartAt = DateTimeOffset.UtcNow.AddSeconds(30);
            logger.LogError(ex, "Lancement du moteur de « {Name} » impossible", name);
        }
    }

    private void OnExited(EngineProcess engine, Process process, string name)
    {
        if (!ReferenceEquals(engine.Process, process)) return;
        int code;
        try { code = process.ExitCode; } catch { code = -1; }
        engine.LastExitCode = code;
        var lived = DateTimeOffset.UtcNow - (engine.StartedAt ?? DateTimeOffset.UtcNow);

        if (!engine.Wanted)
        {
            engine.State = EngineState.Arrete;
        }
        else
        {
            engine.Restarts++;
            // Code 3 = réglage de structure modifié : relance immédiate. Sinon, délai croissant
            // si le moteur plante en boucle (2 s, 4 s, 8 s… jusqu'à 30 s).
            engine.QuickCrashes = code == 3 || lived > TimeSpan.FromSeconds(30) ? 0 : engine.QuickCrashes + 1;
            var delay = code == 3 ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Min(30, 2 * Math.Pow(2, Math.Max(0, engine.QuickCrashes - 1))));
            engine.NextStartAt = DateTimeOffset.UtcNow + delay;
            engine.State = code == 3 ? EngineState.Redemarrage : EngineState.Erreur;
            engine.AddLog(code == 3
                ? "Réglages de la chaîne modifiés : relance du moteur"
                : $"Moteur arrêté (code {code}) — relance dans {delay.TotalSeconds:0} s");
            if (code != 3) logger.LogWarning("Moteur de « {Name} » arrêté (code {Code}), relance dans {Delay} s", name, code, delay.TotalSeconds);
        }
        Changed?.Invoke();
        Wake();
    }

    private static void Kill(EngineProcess engine)
    {
        var process = engine.Process;
        engine.Process = null;
        if (process is null) return;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch { /* déjà arrêté */ }
        process.Dispose();
    }

    /// <summary>Trouve l'exécutable du moteur (installé à côté du serveur, ou compilé dans le dépôt).</summary>
    private string[]? ResolveEngine()
    {
        var configured = options.Value.EnginePath;
        var exe = OperatingSystem.IsWindows() ? "NAR.Engine.exe" : "NAR.Engine";
        var baseDir = AppContext.BaseDirectory;
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configured))
            candidates.Add(Path.GetFullPath(Path.IsPathRooted(configured) ? configured : Path.Combine(baseDir, configured)));
        candidates.Add(Path.GetFullPath(Path.Combine(baseDir, "..", "engine", exe)));
        candidates.Add(Path.Combine(baseDir, exe));
        // Développement : dépôt compilé (src/NAR.Server/bin/<conf>/net8.0 → src/NAR.Engine/bin/<conf>/net8.0).
        var conf = new DirectoryInfo(baseDir).Parent?.Name ?? "Debug";
        candidates.Add(Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "NAR.Engine", "bin", conf, "net8.0", "NAR.Engine.dll")));

        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate)) continue;
            return candidate.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? ["dotnet", candidate] : [candidate];
        }
        return null;
    }
}

/// <summary>Clé partagée entre le serveur et ses moteurs (fichier engine.key dans les données).</summary>
public sealed class EngineKey
{
    public EngineKey(IOptions<NarOptions> options)
    {
        var path = Path.Combine(options.Value.ResolvedDataDirectory, "engine.key");
        if (File.Exists(path))
        {
            Value = File.ReadAllText(path).Trim();
        }
        else
        {
            Value = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Value);
        }
    }

    public string Value { get; }
}
