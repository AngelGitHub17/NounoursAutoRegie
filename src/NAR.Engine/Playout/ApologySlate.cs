using NAR.Core.Media;
using SkiaSharp;

namespace NAR.Engine.Playout;

/// <summary>
/// Carton d'excuse « Désolé, votre programme ne peut pas être diffusé », aux couleurs Rétro 95,
/// avec le logo de la chaîne. Affiché quand rien ne peut passer (pas de bouche-trou) ou quand
/// l'image reste figée trop longtemps. En cas de souci de dessin, la mire de barres reprend le relais.
/// </summary>
public static class ApologySlate
{
    private static readonly SKColor Fond = new(0x0B, 0x0B, 0x0B);
    private static readonly SKColor Blanc = new(0xF7, 0xF7, 0xF2);
    private static readonly SKColor Gris = new(0xB8, 0xB8, 0xB0);
    private static readonly SKColor[] Bande = [new(0xFF, 0x32, 0x00), new(0xFF, 0xA8, 0x00), new(0x00, 0x8A, 0x22), new(0x0B, 0x2B, 0x70), new(0x8C, 0x0A, 0x11)];

    public static byte[] Create(ChannelFormat format, string? logoPath, string channelName, ILogger? logger = null)
    {
        try
        {
            return Render(format, logoPath, channelName);
        }
        catch (Exception ex)
        {
            logger?.LogWarning("Carton d'excuse impossible à dessiner ({Error}) : mire de barres", ex.Message);
            return Slate.CreateColorBars(format);
        }
    }

    private static byte[] Render(ChannelFormat format, string? logoPath, string channelName)
    {
        int w = format.Width, h = format.Height;
        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul))
                            ?? throw new InvalidOperationException("surface Skia indisponible");
        var canvas = surface.Canvas;
        canvas.Clear(Fond);

        // Bande de couleurs Rétro 95 en bas.
        var bandH = h * 0.035f;
        var segment = (float)w / Bande.Length;
        using (var paint = new SKPaint { IsAntialias = false })
        {
            for (var i = 0; i < Bande.Length; i++)
            {
                paint.Color = Bande[i];
                canvas.DrawRect(i * segment, h - bandH, segment + 1, bandH, paint);
            }
        }

        // Logo (ou nom de la chaîne) en haut.
        var top = h * 0.14f;
        SKBitmap? logo = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(logoPath) && File.Exists(logoPath))
            {
                using var raw = SKBitmap.Decode(logoPath);
                if (raw is not null) logo = NAR.Engine.Overlay.OverlayCompositor.TrimTransparent(raw);
            }
        }
        catch { logo = null; }
        if (logo is not null)
        {
            var lh = h * 0.12f;
            var lw = lh * logo.Width / logo.Height;
            if (lw > w * 0.4f) { lh *= w * 0.4f / lw; lw = w * 0.4f; }
            using var p = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
            canvas.DrawBitmap(logo, new SKRect((w - lw) / 2, top, (w + lw) / 2, top + lh), p);
            logo.Dispose();
        }
        else
        {
            using var p = Text("BarlowCondensed-SemiBold.ttf", h * 0.06f, Gris);
            DrawCentered(canvas, channelName.ToUpperInvariant(), w / 2f, top + h * 0.07f, p);
        }

        // Message.
        using (var title = Text("BarlowCondensed-ExtraBoldItalic.ttf", h * 0.072f, Blanc))
        {
            var lines = Wrap("Désolé, votre programme ne peut pas être diffusé", title, w * 0.9f);
            var y = h * 0.5f - (lines.Count - 1) * h * 0.05f;
            foreach (var line in lines)
            {
                DrawCentered(canvas, line, w / 2f, y, title);
                y += h * 0.1f;
            }
        }
        using (var sub = Text("BarlowCondensed-SemiBold.ttf", h * 0.04f, Gris))
        {
            DrawCentered(canvas, "Nous vous prions de nous excuser pour cette interruption.", w / 2f, h * 0.66f, sub);
            DrawCentered(canvas, "Le programme reprendra dès que possible.", w / 2f, h * 0.715f, sub);
        }

        // Liseré orange sous le message.
        using (var bar = new SKPaint { Color = Bande[1] })
            canvas.DrawRect(w * 0.42f, h * 0.57f, w * 0.16f, h * 0.008f, bar);

        using var image = surface.Snapshot();
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        image.ReadPixels(info, bitmap.GetPixels(), info.RowBytes, 0, 0);
        return ToYuv420(bitmap.Bytes, w, h, format.Height >= 720);
    }

    private static SKPaint Text(string font, float size, SKColor color)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Resources", "Fonts", font);
        var face = File.Exists(path) ? SKTypeface.FromFile(path) : SKTypeface.Default;
        return new SKPaint { Typeface = face, TextSize = size, Color = color, IsAntialias = true, SubpixelText = true };
    }

    private static void DrawCentered(SKCanvas canvas, string text, float x, float y, SKPaint paint) =>
        canvas.DrawText(text, x - paint.MeasureText(text) / 2, y, paint);

    private static List<string> Wrap(string text, SKPaint paint, float max)
    {
        var lines = new List<string>();
        var current = "";
        foreach (var word in text.Split(' '))
        {
            var attempt = current.Length == 0 ? word : current + " " + word;
            if (paint.MeasureText(attempt) > max && current.Length > 0)
            {
                lines.Add(current);
                current = word;
            }
            else current = attempt;
        }
        if (current.Length > 0) lines.Add(current);
        return lines;
    }

    private static byte[] ToYuv420(byte[] rgba, int w, int h, bool bt709)
    {
        var frame = new byte[w * h * 3 / 2];
        var (kr, kb) = bt709 ? (0.2126, 0.0722) : (0.299, 0.114);
        var kg = 1 - kr - kb;
        int uBase = w * h, vBase = uBase + w * h / 4;
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = (y * w + x) * 4;
            double r = rgba[i] / 255.0, g = rgba[i + 1] / 255.0, b = rgba[i + 2] / 255.0;
            var luma = kr * r + kg * g + kb * b;
            frame[y * w + x] = (byte)Math.Clamp(Math.Round(16 + 219 * luma), 0, 255);
            if ((x & 1) == 0 && (y & 1) == 0)
            {
                var j = (y / 2) * (w / 2) + x / 2;
                frame[uBase + j] = (byte)Math.Clamp(Math.Round(128 + 224 * (b - luma) / (2 * (1 - kb))), 0, 255);
                frame[vBase + j] = (byte)Math.Clamp(Math.Round(128 + 224 * (r - luma) / (2 * (1 - kr))), 0, 255);
            }
        }
        return frame;
    }
}
