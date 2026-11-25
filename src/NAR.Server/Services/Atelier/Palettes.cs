using SkiaSharp;

namespace NAR.Server.Services.Atelier;

/// <summary>Une palette d'habillage : couleur de cadre + couleurs des cases avec leur poids.</summary>
public sealed record Palette(string Key, string Name, SKColor Frame, (SKColor Color, double Weight)[] Cells, SKColor Label, SKColor LabelText)
{
    public SKColor Pick(Random random)
    {
        var total = Cells.Sum(c => c.Weight);
        var r = random.NextDouble() * total;
        foreach (var (color, weight) in Cells)
        {
            r -= weight;
            if (r <= 0) return color;
        }
        return Cells[^1].Color;
    }
}

/// <summary>
/// Palettes « Rétro 95 » : cadre vert amande et liserés, noir et blanc toujours présents,
/// rouge vif en couleur principale, bordeaux et chocolat en second, vert / marine / orange en touches.
/// </summary>
public static class Palettes
{
    public static readonly SKColor Amande = SKColor.Parse("#E1E8CD");
    public static readonly SKColor Rouge = SKColor.Parse("#FF3200");
    public static readonly SKColor Bordeaux = SKColor.Parse("#8C0A11");
    public static readonly SKColor Chocolat = SKColor.Parse("#3C3632");
    public static readonly SKColor Vert = SKColor.Parse("#008A22");
    public static readonly SKColor Marine = SKColor.Parse("#0B2B70");
    public static readonly SKColor Orange = SKColor.Parse("#FFA800");
    public static readonly SKColor Noir = SKColor.Parse("#0A0A0A");
    public static readonly SKColor Blanc = SKColor.Parse("#FAFAF5");

    public static readonly Palette[] All =
    [
        new("retro95", "Rétro 95", Amande,
            [(Rouge, 30), (Noir, 15), (Blanc, 13), (Bordeaux, 12), (Chocolat, 12), (Vert, 6), (Marine, 6), (Orange, 6)], Orange, Noir),
        new("rouges95", "Rouges 95", Amande,
            [(Rouge, 40), (Bordeaux, 20), (Noir, 15), (Blanc, 10), (Chocolat, 10), (Orange, 5)], Orange, Noir),
        new("orange95", "Orange 95", Amande,
            [(Orange, 35), (Rouge, 15), (Noir, 15), (Blanc, 15), (Chocolat, 12), (Bordeaux, 8)], Rouge, Blanc),
        new("vert95", "Vert 95", Amande,
            [(Vert, 35), (Noir, 15), (Blanc, 15), (Chocolat, 12), (Rouge, 13), (Orange, 10)], Orange, Noir),
        new("marine95", "Marine 95", Amande,
            [(Marine, 35), (Noir, 15), (Blanc, 15), (Rouge, 15), (Chocolat, 10), (Orange, 10)], Orange, Noir),
    ];

    public static Palette Get(string? key) => All.FirstOrDefault(p => p.Key == key) ?? All[0];
}
