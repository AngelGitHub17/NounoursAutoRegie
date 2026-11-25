using System.Text.RegularExpressions;
using NAR.Server.Data;

namespace NAR.Server.Services;

/// <summary>
/// Signalétique jeunesse (-10, -12, -16, -18) : choisie à la main dans la médiathèque, ou
/// déduite de la classification importée (Jellyfin : « FR-12 », « TV-14 », « PG-13 », « R »…).
/// </summary>
public static partial class Signaletique
{
    public static readonly int[] Values = [10, 12, 16, 18];

    public static int? Of(MediaAsset asset) =>
        asset.Signaletique is { } forced ? (forced >= 10 ? forced : null) : FromRating(asset.Rating);

    public static int? FromRating(string? rating)
    {
        if (string.IsNullOrWhiteSpace(rating)) return null;
        var r = rating.Trim().ToUpperInvariant();
        var number = Digits().Match(r);
        if (number.Success && int.TryParse(number.Value, out var age))
        {
            return age switch
            {
                >= 17 => 18,
                >= 15 => 16,
                >= 12 => 12,
                >= 10 => 10,
                _ => null,
            };
        }
        return r switch
        {
            "R" or "TV-MA" => 16,
            "PG" or "TV-PG" => 10,
            _ => null,
        };
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();
}
