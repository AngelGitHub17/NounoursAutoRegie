using NAR.Core.Media;

namespace NAR.Engine.Playout;

/// <summary>
/// Mire de secours générée en mémoire (barres 75 % BT.709), dernier recours quand
/// aucune source ni bouche-trou n'est disponible. Elle ne dépend d'aucun fichier.
/// </summary>
public static class Slate
{
    private static readonly (int R, int G, int B)[] Bars =
    [
        (191, 191, 191), (191, 191, 0), (0, 191, 191), (0, 191, 0),
        (191, 0, 191), (191, 0, 0), (0, 0, 191),
    ];

    public static byte[] CreateColorBars(ChannelFormat format)
    {
        int w = format.Width, h = format.Height;
        var frame = new byte[format.VideoFrameBytes];
        var y = frame.AsSpan(0, w * h);
        var u = frame.AsSpan(w * h, w * h / 4);
        var v = frame.AsSpan(w * h + w * h / 4, w * h / 4);
        var barsHeight = h * 3 / 4;

        for (var row = 0; row < h; row++)
        {
            for (var col = 0; col < w; col++)
            {
                var (yy, cb, cr) = row < barsHeight
                    ? ToYCbCr(Bars[Math.Min(Bars.Length - 1, col * Bars.Length / w)])
                    : ((byte)16, (byte)128, (byte)128); // bande basse noire
                y[row * w + col] = yy;
                if (row % 2 == 0 && col % 2 == 0)
                {
                    var i = (row / 2) * (w / 2) + col / 2;
                    u[i] = cb;
                    v[i] = cr;
                }
            }
        }
        return frame;
    }

    public static byte[] CreateBlack(ChannelFormat format)
    {
        var frame = new byte[format.VideoFrameBytes];
        var luma = format.Width * format.Height;
        frame.AsSpan(0, luma).Fill(16);
        frame.AsSpan(luma).Fill(128);
        return frame;
    }

    private static (byte Y, byte Cb, byte Cr) ToYCbCr((int R, int G, int B) c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        var y = 16 + 219 * (0.2126 * r + 0.7152 * g + 0.0722 * b);
        var cb = 128 + 224 * (-0.1146 * r - 0.3854 * g + 0.5 * b);
        var cr = 128 + 224 * (0.5 * r - 0.4542 * g - 0.0458 * b);
        return (Clamp(y), Clamp(cb), Clamp(cr));
    }

    private static byte Clamp(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);
}
