namespace NAR.Core.Scheduling;

/// <summary>
/// Un élément du conducteur, résolu en images.
/// </summary>
/// <param name="Index">Position dans le conducteur.</param>
/// <param name="Id">Identifiant stable (journal de diffusion).</param>
/// <param name="Title">Titre affiché dans la régie et l'EPG.</param>
/// <param name="Path">Chemin du média.</param>
/// <param name="InFrames">Point d'entrée dans le fichier, en images.</param>
/// <param name="DurationFrames">Durée à diffuser, en images (0 = élément inutilisable).</param>
/// <param name="FixedStartFrame">
/// Horaire fixe (numéro d'image depuis l'époque du moteur). <c>null</c> = enchaîné :
/// l'élément démarre dès que le précédent se termine.
/// </param>
/// <param name="GainDb">Correction de volume appliquée à la diffusion.</param>
public sealed record ScheduledItem(
    int Index,
    string Id,
    string Title,
    string Path,
    long InFrames,
    long DurationFrames,
    long? FixedStartFrame,
    double GainDb = 0)
{
    public bool IsFixed => FixedStartFrame.HasValue;
}
