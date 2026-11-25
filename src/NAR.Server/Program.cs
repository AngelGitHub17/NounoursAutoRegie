using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NAR.Server.Components;
using NAR.Server.Data;
using NAR.Server.Endpoints;
using NAR.Server.Services;

// NounoursAutoRegie — serveur de régie : médiathèque, conducteurs, supervision des moteurs, régie web.

// Outil de diagnostic : analyse rythmique d'un son (NAR.Server --analyser fichier.mp3).
if (args.Length >= 2 && args[0] == "--analyser")
{
    var opts = Microsoft.Extensions.Options.Options.Create(new NarOptions());
    var rhythm = await new NAR.Server.Services.Atelier.AudioAnalyzer(opts).AnalyzeAsync(args[1], CancellationToken.None);
    Console.WriteLine($"Durée {rhythm.Duration:0.00} s · {rhythm.Bpm} BPM · {rhythm.Beats.Length} temps · 1er temps fort : phase {rhythm.DownbeatPhase} · {rhythm.Accents.Length} attaques");
    Console.WriteLine("Temps : " + string.Join(" ", rhythm.Beats.Take(16).Select(b => b.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))));
    var ending = rhythm.Ending(Math.Min(rhythm.Duration, 20));
    Console.WriteLine($"Fin : {ending.Label} · musique jusqu'à {ending.MusicEnd:0.00} s · dernier coup {ending.FinalHit:0.00} s");
    if (args.Length >= 3)
    {
        var job = new NAR.Server.Services.Atelier.AtelierJob(
            Enum.Parse<NAR.Server.Services.Atelier.AtelierKind>(args.Length >= 4 ? args[3] : "Jingle"),
            "Le Grand Film", "Ce soir · 20H50", "Cinéma", NAR.Server.Services.Atelier.Palettes.Get("retro95"),
            args[1], rhythm, args.Length >= 5 ? args[4] : null, false, 1280, 720, new NAR.Core.Media.FrameRate(25),
            Math.Min(rhythm.Duration, 20), args.Length >= 6 ? args[5] : null, [5, 20, 35, 50], 42, Path.GetFullPath(args[2]));
        await new NAR.Server.Services.Atelier.Retro95Renderer(opts, Microsoft.Extensions.Logging.Abstractions.NullLogger<NAR.Server.Services.Atelier.Retro95Renderer>.Instance)
            .RenderAsync(job, null, CancellationToken.None);
        Console.WriteLine($"Rendu : {args[2]}");
    }
    return;
}
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });

// Exécution depuis la sortie de compilation (développement) : les fichiers web viennent du manifeste.
if (!Directory.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot")))
    builder.WebHost.UseStaticWebAssets();

builder.Services.Configure<NarOptions>(builder.Configuration.GetSection("Nar"));
var nar = builder.Configuration.GetSection("Nar").Get<NarOptions>() ?? new NarOptions();
var dataDir = nar.ResolvedDataDirectory;
Directory.CreateDirectory(dataDir);

if (string.IsNullOrEmpty(builder.Configuration["ASPNETCORE_URLS"]) && string.IsNullOrEmpty(builder.Configuration["urls"]))
    builder.WebHost.UseUrls(nar.PublicPort > 0 && nar.PublicPort != nar.HttpPort
        ? [$"http://0.0.0.0:{nar.HttpPort}", $"http://0.0.0.0:{nar.PublicPort}"]
        : [$"http://0.0.0.0:{nar.HttpPort}"]);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.DataProtection", LogLevel.Error);
builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);

builder.Services.AddDbContextFactory<NarDbContext>(o => o.UseSqlite($"Data Source={Path.Combine(dataDir, "nar.db")}"));
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys")))
    .SetApplicationName("NounoursAutoRegie");

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/connexion";
        o.LogoutPath = "/deconnexion";
        o.AccessDeniedPath = "/connexion";
        o.Cookie.Name = "nar_session";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.SlidingExpiration = true;
        o.ExpireTimeSpan = TimeSpan.FromDays(30);
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddHttpClient("engines", c => c.Timeout = TimeSpan.FromSeconds(3));
builder.Services.AddHttpClient("live", c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddHttpClient("jellyfin", c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddHttpClient("jellyfin-stream", c => c.Timeout = Timeout.InfiniteTimeSpan);

builder.Services.AddSingleton<EngineKey>();
builder.Services.AddSingleton<FfmpegTools>();
builder.Services.AddSingleton<NAR.Server.Services.Network.SmbSources>();
builder.Services.AddSingleton<NAR.Server.Services.Network.NetworkScanner>();
builder.Services.AddSingleton<MediaLibrary>();
builder.Services.AddSingleton<EngineConfigBuilder>();
builder.Services.AddSingleton<Accounts>();
builder.Services.AddSingleton<Seeder>();
builder.Services.AddSingleton<ScheduleService>();
builder.Services.AddSingleton<NAR.Server.Services.Atelier.AudioAnalyzer>();
builder.Services.AddSingleton<NAR.Server.Services.Atelier.Retro95Renderer>();
builder.Services.AddSingleton<NAR.Server.Services.Atelier.AtelierService>();
builder.Services.AddHostedService<NAR.Server.Services.Atelier.AtelierWorker>();
builder.Services.AddSingleton<Diffusion>();
builder.Services.AddSingleton<ProgrammeTypes>();
builder.Services.AddSingleton<EngineSupervisor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<EngineSupervisor>());
builder.Services.AddSingleton<EngineMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<EngineMonitor>());
builder.Services.AddHostedService<IngestWorker>();
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(15));

var app = builder.Build();

// Base de données : création / mise à jour du schéma, puis chaîne de démonstration au premier lancement.
await using (var db = await app.Services.GetRequiredService<IDbContextFactory<NarDbContext>>().CreateDbContextAsync())
{
    await db.Database.MigrateAsync();
}
await app.Services.GetRequiredService<Seeder>().SeedAsync();
await app.Services.GetRequiredService<ProgrammeTypes>().SeedAsync();
app.Logger.LogInformation("NounoursAutoRegie {Version} — données dans {Data}",
    typeof(Program).Assembly.GetName().Version?.ToString(3), dataDir);

app.UseExceptionHandler("/erreur", createScopeForErrors: true);

// Port de diffusion (ouvert sur Internet) : uniquement les flux /live/…, et seulement avec la clé.
if (nar.PublicPort > 0 && nar.PublicPort != nar.HttpPort)
{
    app.Use(async (context, next) =>
    {
        if (context.Connection.LocalPort == nar.PublicPort)
        {
            if (!context.Request.Path.StartsWithSegments("/live"))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            if (!context.RequestServices.GetRequiredService<Diffusion>().KeyMatches(context.Request.Query["cle"]))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("clé de diffusion manquante ou invalide");
                return;
            }
        }
        await next();
    });
}

// Tant qu'aucun compte n'existe, toute la régie mène à la page d'installation.
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    var free = path.StartsWithSegments("/installation") || path.StartsWithSegments("/_framework")
               || path.StartsWithSegments("/_blazor") || path.StartsWithSegments("/api/engine")
               || path.StartsWithSegments("/live") || path.StartsWithSegments("/interne") || path.StartsWithSegments("/css") || path.StartsWithSegments("/js")
               || Path.HasExtension(path.Value);
    if (!free && !await context.RequestServices.GetRequiredService<Accounts>().HasUsersAsync())
    {
        context.Response.Redirect("/installation");
        return;
    }
    await next();
});

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapEngineApi();
app.MapLive();
app.MapEpg();
app.MapMediaFiles();
app.MapSmbRelay();
app.MapJellyfinRelay();
app.MapAtelierFiles();
app.MapAccountEndpoints();
app.MapDiffusion();

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();
