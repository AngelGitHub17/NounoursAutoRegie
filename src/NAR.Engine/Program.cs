using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using NAR.Core.Config;
using NAR.Engine;
using NAR.Engine.Config;
using NAR.Engine.Playout;

// NounoursAutoRegie — moteur de diffusion d'une chaîne.
//
//   Mode autonome :  NAR.Engine <chemin/vers/channel.json>
//   Mode régie    :  NAR.Engine --server http://127.0.0.1:8090 --channel <id>
//                    (clé d'accès dans la variable NAR_ENGINE_KEY ; lancé par NAR.Server)
string? Arg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

var serverUrl = Arg("--server") ?? Environment.GetEnvironmentVariable("NAR_SERVER");
var channelId = Arg("--channel") ?? Environment.GetEnvironmentVariable("NAR_CHANNEL");
var configPath = args.FirstOrDefault(a => !a.StartsWith('-') && a != serverUrl && a != channelId)
                 ?? Environment.GetEnvironmentVariable("NAR_CONFIG")
                 ?? "channel.json";

using var bootLogging = LoggerFactory.Create(b => b.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; }));
IChannelConfigSource source;
if (!string.IsNullOrWhiteSpace(serverUrl) && !string.IsNullOrWhiteSpace(channelId))
{
    source = new RemoteConfigSource(serverUrl, channelId, Environment.GetEnvironmentVariable("NAR_ENGINE_KEY"),
        bootLogging.CreateLogger<RemoteConfigSource>());
}
else
{
    if (!File.Exists(configPath))
    {
        Console.Error.WriteLine($"Configuration introuvable : {Path.GetFullPath(configPath)}");
        Console.Error.WriteLine("Usage : NAR.Engine <channel.json>   ou   NAR.Engine --server <url> --channel <id>");
        return 1;
    }
    source = new FileConfigSource(configPath, bootLogging.CreateLogger<FileConfigSource>());
}

using var startupCts = new CancellationTokenSource();
Console.CancelKeyPress += (_, _) => startupCts.Cancel();
ChannelConfig config;
try
{
    config = await source.LoadInitialAsync(startupCts.Token);
}
catch (OperationCanceledException)
{
    return 0;
}

var hlsDirectory = config.Resolve(config.Hls.Directory);
Directory.CreateDirectory(hlsDirectory);

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.WebHost.UseUrls($"http://{config.StatusBind}:{config.StatusPort}");
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning);

builder.Services.AddSingleton(config);
builder.Services.AddSingleton(source);
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

await app.RunAsync();
return Environment.ExitCode;
