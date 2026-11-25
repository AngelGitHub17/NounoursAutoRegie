using Microsoft.EntityFrameworkCore;
using NAR.Server.Data;
using NAR.Server.Services.Atelier;

namespace NAR.Server.Endpoints;

/// <summary>Écoute des sons, aperçu des créations et logos de chaîne (connexion requise).</summary>
public static class AtelierFiles
{
    public static void MapAtelierFiles(this WebApplication app)
    {
        app.MapGet("/atelier/sons/{id:int}", async (int id, IDbContextFactory<NarDbContext> dbFactory, AtelierService atelier) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var sound = await db.AtelierSounds.FindAsync(id);
            if (sound is null) return Results.NotFound();
            var path = Path.Combine(atelier.SoundsDirectory, sound.FileName);
            return File.Exists(path) ? Results.File(path, ContentType(path), enableRangeProcessing: true) : Results.NotFound();
        }).RequireAuthorization();

        app.MapGet("/atelier/creations/{id:int}.mp4", async (int id, IDbContextFactory<NarDbContext> dbFactory, AtelierService atelier) =>
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var creation = await db.AtelierCreations.FindAsync(id);
            if (creation is not { Status: CreationStatus.Pret }) return Results.NotFound();
            var file = Directory.EnumerateFiles(atelier.CreationsDirectory, $"*-{id}.mp4").FirstOrDefault();
            return file is null ? Results.NotFound() : Results.File(file, "video/mp4", enableRangeProcessing: true);
        }).RequireAuthorization();

        app.MapGet("/chaines/{id:int}/logo.png", (int id, AtelierService atelier) =>
        {
            var path = atelier.LogoPath(id);
            return File.Exists(path) ? Results.File(path, "image/png", lastModified: File.GetLastWriteTimeUtc(path)) : Results.NotFound();
        }).RequireAuthorization();
    }

    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp3" => "audio/mpeg",
        ".wav" => "audio/wav",
        ".ogg" or ".oga" or ".opus" => "audio/ogg",
        ".flac" => "audio/flac",
        ".m4a" or ".aac" => "audio/mp4",
        _ => "application/octet-stream",
    };
}
