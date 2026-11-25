using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using NAR.Core.Config;
using NAR.Engine;
using NAR.Engine.Playout;

// NounoursAutoRegie — moteur de diffusion d'une chaîne (phase 0).
// Usage : NAR.Engine <chemin/vers/channel.json>
var configPath = args.FirstOrDefault(a => !a.StartsWith('-'))
                 ?? Environment.GetEnvironmentVariable("NAR_CONFIG")
                 ?? "channel.json";

if (!File.Exists(configPath))
{
    Console.Error.WriteLine($"Configuration introuvable : {Path.GetFullPath(configPath)}");
    Console.Error.WriteLine("Usage : NAR.Engine <channel.json>   (exemple dans samples/channel.example.json)");
    return 1;
}

var config = ChannelConfig.Load(configPath);
var hlsDirectory = config.Resolve(config.Hls.Directory);
Directory.CreateDirectory(hlsDirectory);

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.WebHost.UseUrls($"http://0.0.0.0:{config.StatusPort}");
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

builder.Services.AddSingleton(config);
builder.Services.AddSingleton<EngineStatus>();
builder.Services.AddSingleton(sp => new MediaProbe(config.FfprobePath, sp.GetRequiredService<ILogger<MediaProbe>>()));
builder.Services.AddHostedService<PlayoutEngine>();
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(15));

var app = builder.Build();

var contentTypes = new FileExtensionContentTypeProvider();
contentTypes.Mappings[".m3u8"] = "application/vnd.apple.mpegurl";
contentTypes.Mappings[".ts"] = "video/mp2t";

app.Use(async (context, next) =>
{
    // La page de lecture et NounoursTV peuvent lire le flux depuis un autre domaine.
    context.Response.Headers.AccessControlAllowOrigin = "*";
    if (context.Request.Path.StartsWithSegments("/hls"))
        context.Response.Headers.CacheControl = context.Request.Path.Value!.EndsWith(".m3u8") ? "no-cache" : "max-age=60";
    await next();
});
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(hlsDirectory),
    RequestPath = "/hls",
    ContentTypeProvider = contentTypes,
});

app.MapGet("/status", (EngineStatus status) => Results.Json(status.Snapshot));
app.MapGet("/", () => Results.Content(StatusPage.Html(config.Name), "text/html; charset=utf-8"));

app.Run();
return 0;
