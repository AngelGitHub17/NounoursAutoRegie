using System.Security.Claims;
using NAR.Server.Data;

namespace NAR.Server.Components.Shared;

/// <summary>
/// Qui peut faire quoi dans la régie. Vérifié côté serveur dans chaque action
/// (pas seulement en masquant les boutons).
/// </summary>
public static class Perm
{
    /// <summary>Allumer, éteindre, redémarrer une chaîne.</summary>
    public static bool Antenna(ClaimsPrincipal u) => u.IsInRole(Roles.Admin) || u.IsInRole(Roles.Regisseur);

    /// <summary>Modifier un conducteur (le régisseur peut intervenir en direct).</summary>
    public static bool Playlist(ClaimsPrincipal u) => u.IsInRole(Roles.Admin) || u.IsInRole(Roles.Programmateur) || u.IsInRole(Roles.Regisseur);

    /// <summary>Gérer la médiathèque (dossiers, partages, Jellyfin, fiches).</summary>
    public static bool Library(ClaimsPrincipal u) => u.IsInRole(Roles.Admin) || u.IsInRole(Roles.Programmateur);

    /// <summary>Créer et régler les chaînes, gérer les comptes.</summary>
    public static bool Admin(ClaimsPrincipal u) => u.IsInRole(Roles.Admin);
}
