using NAR.Core.Config;
using NAR.Core.Media;
using SkiaSharp;

namespace NAR.Engine.Overlay;

/// <summary>Ce que l'habillage doit savoir de l'antenne à l'image en cours.</summary>
/// <param name="WallTime">Heure murale de l'image (pour l'horloge).</param>
/// <param name="OnAir">Un élément (ou le bouche-trou) est à l'antenne ; faux sur la mire.</param>
/// <param name="Overlay">L'élément en cours accepte l'habillage (faux pour les pubs, jingles…).</param>
/// <param name="IsProgramme">L'élément en cours est un programme.</param>
/// <param name="Rating">Signalétique de l'élément en cours.</param>
/// <param name="ElapsedSeconds">Temps écoulé depuis le début de l'élément (position dans le média).</param>
/// <param name="RemainingSeconds">Temps restant avant la fin (ou la coupure) de l'élément.</param>
/// <param name="NextTitle">Titre du prochain programme.</param>
/// <param name="NextStart">Heure de début prévue du prochain programme.</param>
public readonly record struct OverlayContext(
    DateTimeOffset WallTime,
    bool OnAir,
    bool Overlay,
    bool IsProgramme,
    int? Rating,
    double ElapsedSeconds,
    double RemainingSeconds,
    string? NextTitle,
    DateTimeOffset? NextStart);

/// <summary>
/// Habillage incrusté sur l'antenne. Les éléments (logo, horloge, bandeau « À suivre »,
/// pictogramme de signalétique) sont dessinés avec Skia une seule fois — puis à chaque fois que
/// leur texte change — convertis en YUV 4:2:0 avec leur transparence, et mélangés directement
/// dans l'image brute envoyée à l'encodeur. Le coût par image se limite à la surface des éléments.
/// </summary>
public sealed class OverlayCompositor : IDisposable
{
    private static readonly SKColor Noir = new(0x0B, 0x0B, 0x0B);
    private static readonly SKColor Blanc = new(0xF7, 0xF7, 0xF2);
    private static readonly SKColor Rouge = new(0xFF, 0x32, 0x00);
    private static readonly SKColor Orange = new(0xFF, 0xA8, 0x00);

    private static readonly Lazy<SKTypeface> TitleFace = new(() => LoadFace("BarlowCondensed-ExtraBoldItalic.ttf", SKFontStyleWeight.ExtraBold, SKFontStyleSlant.Italic));
    private static readonly Lazy<SKTypeface> LabelFace = new(() => LoadFace("BarlowCondensed-SemiBold.ttf", SKFontStyleWeight.SemiBold, SKFontStyleSlant.Upright));

    private readonly ChannelFormat _format;
    private readonly ILogger _logger;
    private readonly bool _hd;
    private OverlayConfig _config = new() { UpNext = false, Rating = false };
    private string? _logoKey;
    private Sprite? _logo;
    private string? _clockText;
    private Sprite? _clock;
    private string? _upNextKey;
    private Sprite? _upNext;
    private readonly Dictionary<int, Sprite> _ratings = new();

    public OverlayCompositor(ChannelFormat format, ILogger logger)
    {
        _format = format;
        _logger = logger;
        _hd = format.Height >= 720;
    }

    private float H => _format.Height;
    private float Margin => (float)Math.Round(H * 0.035);
    private bool Left => _config.Corner.Contains("gauche", StringComparison.OrdinalIgnoreCase);
    private bool Bottom => _config.Corner.StartsWith("bas", StringComparison.OrdinalIgnoreCase);

    /// <summary>Applique de nouveaux réglages (au rechargement du conducteur). Sans effet s'ils n'ont pas changé.</summary>
    public void Configure(OverlayConfig config, Func<string, string> resolve)
    {
        var logoKey = string.IsNullOrWhiteSpace(config.Logo)
            ? null
            : $"{resolve(config.Logo!)}|{config.LogoVersion}|{config.LogoHeightPercent}|{config.LogoOpacity}";
        var layoutChanged = config.Corner != _config.Corner;
        _config = config;
        if (logoKey != _logoKey || layoutChanged)
        {
            _logoKey = logoKey;
            _logo = logoKey is null ? null : LoadLogo(resolve(config.Logo!));
        }
        if (layoutChanged)
        {
            _clockText = null;
            _upNextKey = null;
        }
    }

    /// <summary>Faut-il habiller cette image ? (sinon l'image source part telle quelle, sans copie.)</summary>
    public bool IsActive(in OverlayContext ctx) => ctx.OnAir && _config.Any;

    /// <summary>Mélange l'habillage dans l'image YUV 4:2:0 <paramref name="frame"/>.</summary>
    public void Apply(byte[] frame, in OverlayContext ctx)
    {
        if (!IsActive(ctx)) return;

        // Fondu de 0,4 s à l'entrée et à la sortie de chaque élément habillé.
        var show = ctx.Overlay ? Math.Clamp(Math.Min(ctx.ElapsedSeconds, ctx.RemainingSeconds) / 0.4, 0, 1) : 0;
        if (show <= 0) return;

        var w = _format.Width;
        var margin = (int)Margin;
        var y = margin;

        // Logo et horloge, empilés dans le coin choisi.
        var stack = new List<(Sprite Sprite, double Alpha)>();
        if (_logo is not null) stack.Add((_logo, show * Math.Clamp(_config.LogoOpacity, 10, 100) / 100.0));
        if (_config.Clock)
        {
            var text = ctx.WallTime.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
            if (text != _clockText || _clock is null)
            {
                _clockText = text;
                _clock = RenderClock(text);
            }
            stack.Add((_clock, show * 0.92));
        }
        if (Bottom) stack.Reverse();
        var gap = (int)(H * 0.006);
        var total = stack.Sum(s => s.Sprite.Height) + Math.Max(0, stack.Count - 1) * gap;
        y = Bottom ? _format.Height - margin - total : margin;
        foreach (var (sprite, alpha) in stack)
        {
            var x = Left ? margin : w - margin - sprite.Width;
            Blend(frame, sprite, x, y, alpha);
            y += sprite.Height + gap;
        }

        // Signalétique : en bas du côté du logo (en haut si le logo est en bas).
        if (_config.Rating && ctx.Rating is >= 10 and var rating && (_config.RatingAlways || ctx.ElapsedSeconds < 60))
        {
            if (!_ratings.TryGetValue(rating, out var picto)) _ratings[rating] = picto = RenderRating(rating);
            var px = Left ? margin : w - margin - picto.Width;
            var py = Bottom ? margin : _format.Height - margin - picto.Height;
            // Apparition et disparition en fondu, même en mode « première minute ».
            var ratingAlpha = _config.RatingAlways ? show : show * Math.Clamp((60 - ctx.ElapsedSeconds) / 0.4, 0, 1);
            Blend(frame, picto, px, py, ratingAlpha);
        }

        // « À suivre » : glisse depuis le bord opposé au logo, le temps prévu avant la fin du programme.
        if (_config.UpNext && ctx.IsProgramme && ctx.NextTitle is not null)
        {
            var start = _config.UpNextBeforeEndSeconds;
            var duration = Math.Max(3, _config.UpNextDurationSeconds);
            var into = start - ctx.RemainingSeconds;
            if (into >= 0 && into < duration && ctx.ElapsedSeconds > 5)
            {
                var when = ctx.NextStart is { } ns && ns - ctx.WallTime < TimeSpan.FromHours(12)
                    ? ns.ToString("HH'H'mm", System.Globalization.CultureInfo.InvariantCulture)
                    : null;
                var key = $"{ctx.NextTitle}|{when}";
                if (key != _upNextKey || _upNext is null)
                {
                    _upNextKey = key;
                    _upNext = RenderUpNext(ctx.NextTitle, when);
                }
                var slideIn = Ease(Math.Clamp(into / 0.45, 0, 1));
                var slideOut = Ease(Math.Clamp((duration - into) / 0.45, 0, 1));
                var progress = Math.Min(slideIn, slideOut);
                var fromLeft = !Left;
                var restX = fromLeft ? margin : w - margin - _upNext.Width;
                var hiddenX = fromLeft ? -_upNext.Width : w;
                var x = (int)Math.Round(hiddenX + (restX - hiddenX) * progress);
                var by = _format.Height - margin - _upNext.Height;
                Blend(frame, _upNext, x, by, Math.Min(1, progress * 1.5));
            }
        }
    }

    private static double Ease(double x) => 1 - Math.Pow(1 - x, 3);

    // --- Dessin des éléments ------------------------------------------------------------------

    private Sprite? LoadLogo(string path)
    {
        try
        {
            using var decoded = SKBitmap.Decode(path);
            if (decoded is null)
            {
                _logger.LogWarning("Logo illisible : {Path}", path);
                return null;
            }
            // Les bords transparents du fichier sont retirés : le logo se cale vraiment dans le coin
            // et contre l'horloge, quelle que soit la marge laissée dans le PNG.
            using var source = TrimTransparent(decoded);
            var height = Math.Max(8, H * Math.Clamp(_config.LogoHeightPercent, 3, 25) / 100.0);
            var width = height * source.Width / source.Height;
            var maxWidth = _format.Width * 0.22;
            if (width > maxWidth)
            {
                height *= maxWidth / width;
                width = maxWidth;
            }
            using var surface = Surface((int)Math.Ceiling(width), (int)Math.Ceiling(height));
            using var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
            surface.Canvas.DrawBitmap(source, new SKRect(0, 0, (float)width, (float)height), paint);
            _logger.LogInformation("Logo d'antenne chargé ({W}×{H})", (int)width, (int)height);
            return Sprite.From(surface, _hd);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Logo illisible ({Path}) : {Error}", path, ex.Message);
            return null;
        }
    }

    internal static SKBitmap TrimTransparent(SKBitmap bitmap)
    {
        int w = bitmap.Width, h = bitmap.Height;
        int left = w, top = h, right = -1, bottom = -1;
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            if (bitmap.GetPixel(x, y).Alpha <= 8) continue;
            if (x < left) left = x;
            if (x > right) right = x;
            if (y < top) top = y;
            if (y > bottom) bottom = y;
        }
        var copy = new SKBitmap();
        if (right < 0 || (left == 0 && top == 0 && right == w - 1 && bottom == h - 1))
        {
            bitmap.CopyTo(copy);
            return copy;
        }
        bitmap.ExtractSubset(copy, new SKRectI(left, top, right + 1, bottom + 1));
        var result = copy.Copy();
        copy.Dispose();
        return result;
    }

    private Sprite RenderClock(string text)
    {
        var height = H * 0.042f;
        using var paint = TextPaint(LabelFace.Value, height * 0.78f, Blanc);
        var pad = height * 0.32f;
        var width = paint.MeasureText(text) + pad * 2;
        using var surface = Surface((int)Math.Ceiling(width), (int)Math.Ceiling(height));
        var canvas = surface.Canvas;
        using var box = new SKPaint { Color = Noir, IsAntialias = true };
        canvas.DrawRect(0, 0, width, height, box);
        using var bar = new SKPaint { Color = Rouge };
        canvas.DrawRect(0, height - height * 0.1f, width, height * 0.1f, bar);
        canvas.DrawText(text, pad, height * 0.78f, paint);
        return Sprite.From(surface, _hd);
    }

    private Sprite RenderRating(int rating)
    {
        var size = H * 0.075f;
        using var surface = Surface((int)Math.Ceiling(size), (int)Math.Ceiling(size));
        var canvas = surface.Canvas;
        var c = size / 2;
        using var disc = new SKPaint { Color = SKColors.White, IsAntialias = true };
        canvas.DrawCircle(c, c, c - 1, disc);
        using var ring = new SKPaint { Color = SKColors.Black, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = size * 0.05f };
        canvas.DrawCircle(c, c, c - size * 0.06f, ring);
        var text = $"-{rating}";
        using var paint = TextPaint(TitleFace.Value, size * 0.5f, SKColors.Black);
        var tw = paint.MeasureText(text);
        canvas.DrawText(text, c - tw / 2, c + size * 0.16f, paint);
        return Sprite.From(surface, _hd);
    }

    /// <summary>Bandeau Rétro 95 : étiquette orange « À SUIVRE · 20H50 » posée sur un cartouche noir au titre blanc.</summary>
    private Sprite RenderUpNext(string title, string? when)
    {
        var titleText = title.Trim().ToUpperInvariant();
        var labelText = when is null ? "À SUIVRE" : $"À SUIVRE · {when}";
        var titleH = H * 0.07f;
        var labelH = H * 0.038f;
        using var titlePaint = TextPaint(TitleFace.Value, titleH * 0.72f, Blanc);
        using var labelPaint = TextPaint(LabelFace.Value, labelH * 0.78f, Noir);
        var maxTitle = _format.Width * 0.55f;
        var measured = titlePaint.MeasureText(titleText);
        if (measured > maxTitle)
        {
            titlePaint.TextSize *= maxTitle / measured;
            measured = titlePaint.MeasureText(titleText);
        }
        var bar = titleH * 0.14f;
        var pad = titleH * 0.3f;
        var titleW = bar + pad * 2 + measured;
        var labelW = labelPaint.MeasureText(labelText) + labelH * 0.6f;
        var width = Math.Max(titleW, labelW + bar);
        var height = labelH + titleH;
        using var surface = Surface((int)Math.Ceiling(width), (int)Math.Ceiling(height));
        var canvas = surface.Canvas;
        using var fill = new SKPaint { IsAntialias = true };
        fill.Color = Orange;
        canvas.DrawRect(bar, 0, labelW, labelH, fill);
        canvas.DrawText(labelText, bar + labelH * 0.3f, labelH * 0.8f, labelPaint);
        fill.Color = Noir;
        canvas.DrawRect(0, labelH, titleW, titleH, fill);
        fill.Color = Rouge;
        canvas.DrawRect(0, labelH, bar, titleH, fill);
        canvas.DrawText(titleText, bar + pad, labelH + titleH * 0.76f, titlePaint);
        return Sprite.From(surface, _hd);
    }

    private static SKSurface Surface(int width, int height) =>
        SKSurface.Create(new SKImageInfo(Math.Max(2, width + (width & 1)), Math.Max(2, height + (height & 1)), SKColorType.Rgba8888, SKAlphaType.Premul))
        ?? throw new InvalidOperationException("Surface Skia indisponible");

    private static SKPaint TextPaint(SKTypeface face, float size, SKColor color) =>
        new() { Typeface = face, TextSize = size, Color = color, IsAntialias = true, SubpixelText = true };

    private static SKTypeface LoadFace(string file, SKFontStyleWeight weight, SKFontStyleSlant slant)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Resources", "Fonts", file);
        return File.Exists(path)
            ? SKTypeface.FromFile(path)
            : SKTypeface.FromFamilyName("DejaVu Sans Condensed", weight, SKFontStyleWidth.Condensed, slant) ?? SKTypeface.Default;
    }

    // --- Mélange YUV 4:2:0 ------------------------------------------------------------------------

    private void Blend(byte[] frame, Sprite sprite, int x, int y, double alpha)
    {
        if (alpha <= 0.002) return;
        x &= ~1;
        y &= ~1;
        int fw = _format.Width, fh = _format.Height;
        var x0 = Math.Max(0, x);
        var y0 = Math.Max(0, y);
        var x1 = Math.Min(fw, x + sprite.Width);
        var y1 = Math.Min(fh, y + sprite.Height);
        if (x0 >= x1 || y0 >= y1) return;
        var k = (int)Math.Round(Math.Clamp(alpha, 0, 1) * 256);

        for (var row = y0; row < y1; row++)
        {
            var src = (row - y) * sprite.Width - x;
            var dst = row * fw;
            for (var col = x0; col < x1; col++)
            {
                var a = sprite.A[src + col] * k >> 8;
                if (a == 0) continue;
                ref var d = ref frame[dst + col];
                d = (byte)(d + ((sprite.Y[src + col] - d) * a + 127) / 255);
            }
        }

        var cw = fw / 2;
        var uBase = fw * fh;
        var vBase = uBase + cw * (fh / 2);
        var sw = sprite.Width / 2;
        for (var row = y0 / 2; row < (y1 + 1) / 2; row++)
        {
            var src = (row - y / 2) * sw - x / 2;
            var dst = row * cw;
            for (var col = x0 / 2; col < (x1 + 1) / 2; col++)
            {
                var a = sprite.Ac[src + col] * k >> 8;
                if (a == 0) continue;
                ref var u = ref frame[uBase + dst + col];
                u = (byte)(u + ((sprite.U[src + col] - u) * a + 127) / 255);
                ref var v = ref frame[vBase + dst + col];
                v = (byte)(v + ((sprite.V[src + col] - v) * a + 127) / 255);
            }
        }
    }

    public void Dispose() { }

    /// <summary>Élément d'habillage pré-converti : luminance + alpha pleine résolution, chrominance + alpha au quart.</summary>
    private sealed class Sprite
    {
        public int Width, Height;
        public byte[] Y = [], A = [], U = [], V = [], Ac = [];

        public static Sprite From(SKSurface surface, bool bt709)
        {
            using var image = surface.Snapshot();
            var info = new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            using var bitmap = new SKBitmap(info);
            image.ReadPixels(info, bitmap.GetPixels(), info.RowBytes, 0, 0);
            var pixels = bitmap.Bytes;
            int w = image.Width, h = image.Height;
            var s = new Sprite { Width = w, Height = h, Y = new byte[w * h], A = new byte[w * h], U = new byte[w * h / 4], V = new byte[w * h / 4], Ac = new byte[w * h / 4] };
            var (kr, kb) = bt709 ? (0.2126, 0.0722) : (0.299, 0.114);
            var kg = 1 - kr - kb;
            var cb = new double[w * h];
            var cr = new double[w * h];
            for (var i = 0; i < w * h; i++)
            {
                double r = pixels[i * 4] / 255.0, g = pixels[i * 4 + 1] / 255.0, b = pixels[i * 4 + 2] / 255.0;
                var luma = kr * r + kg * g + kb * b;
                s.Y[i] = Clamp(16 + 219 * luma);
                s.A[i] = pixels[i * 4 + 3];
                cb[i] = (b - luma) / (2 * (1 - kb));
                cr[i] = (r - luma) / (2 * (1 - kr));
            }
            // Chrominance : moyenne 2×2 pondérée par l'alpha (pas de liseré sombre autour des bords transparents).
            for (var yy = 0; yy < h / 2; yy++)
            for (var xx = 0; xx < w / 2; xx++)
            {
                double sa = 0, su = 0, sv = 0;
                for (var dy = 0; dy < 2; dy++)
                for (var dx = 0; dx < 2; dx++)
                {
                    var i = (yy * 2 + dy) * w + xx * 2 + dx;
                    var a = s.A[i];
                    sa += a;
                    su += cb[i] * a;
                    sv += cr[i] * a;
                }
                var j = yy * (w / 2) + xx;
                s.Ac[j] = (byte)Math.Round(sa / 4);
                s.U[j] = sa > 0 ? Clamp(128 + 224 * su / sa) : (byte)128;
                s.V[j] = sa > 0 ? Clamp(128 + 224 * sv / sa) : (byte)128;
            }
            return s;
        }

        private static byte Clamp(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);
    }
}
