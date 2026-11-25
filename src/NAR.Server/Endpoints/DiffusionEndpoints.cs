using System.Text;
using NAR.Server.Data;
using NAR.Server.Services;

namespace NAR.Server.Endpoints;

public static class DiffusionEndpoints
{
    /// <summary>Téléchargement du relais nar.php, prêt à déposer dans le site Nounours.TV (administrateurs).</summary>
    public static void MapDiffusion(this WebApplication app)
    {
        app.MapGet("/diffusion/nar.php", (Diffusion diffusion) =>
            Results.File(Encoding.UTF8.GetBytes(diffusion.NarPhp()), "application/x-httpd-php", "nar.php"))
            .RequireAuthorization(p => p.RequireRole(Roles.Admin));
    }
}
