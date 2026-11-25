using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NAR.Server.Components;
using NAR.Server.Data;
using NAR.Server.Endpoints;
using NAR.Server.Services;

// NounoursAutoRegie — serveur de régie : médiathèque, conducteurs, supervision des moteurs, régie web.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });

// Exécution depuis la sortie de compilation (développement) : les fichiers web viennent du manifeste.
if (!Directory.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot")))
    builder.WebHost.UseStaticWebAssets();

builder.Services.Configure<NarOptions>(builder.Configuration.GetSection("Nar"));
var nar = builder.Configuration.GetSection("Nar").Get<NarOptions>() ?? new NarOptions();
var dataDir = nar.ResolvedDataDirectory;
Directory.CreateDirectory(dataDir);

if (string.IsNullOrEmpty(builder.Configuration["ASPNETCORE_URLS"]) && string.IsNullOrEmpty(builder.Configuration["urls"]))
    builder.WebHost.UseUrls($"http://0.0.0.0:{nar.HttpPort}");

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

builder.Services.AddSingleton<EngineKey>();
builder.Services.AddSingleton<FfmpegTools>();
builder.Services.AddSingleton<NAR.Server.Services.Network.SmbSources>();
builder.Services.AddSingleton<NAR.Server.Services.Network.NetworkScanner>();
builder.Services.AddSingleton<MediaLibrary>();
builder.Services.AddSingleton<EngineConfigBuilder>();
builder.Services.AddSingleton<Accounts>();
builder.Services.AddSingleton<Seeder>();
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
app.Logger.LogInformation("NounoursAutoRegie {Version} — données dans {Data}",
    typeof(Program).Assembly.GetName().Version?.ToString(3), dataDir);

app.UseExceptionHandler("/erreur", createScopeForErrors: true);

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
app.MapMediaFiles();
app.MapSmbRelay();
app.MapAccountEndpoints();

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();
