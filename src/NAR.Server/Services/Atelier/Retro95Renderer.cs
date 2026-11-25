using System.Diagnostics;
using System.Globalization;
using NAR.Core.Media;
using SkiaSharp;

namespace NAR.Server.Services.Atelier;

public enum AtelierKind
{
    /// <summary>Jingle avant programme (étiquette + titre + logo).</summary>
    Jingle,
    /// <summary>Identité de chaîne (logo au centre de la mosaïque).</summary>
    Ident,
    /// <summary>Générique de début ou de fin d'émission.</summary>
    Generique,
    /// <summary>Bande-annonce : extraits d'un programme dans la mosaïque.</summary>
    BandeAnnonce,
}

/// <summary>Tout ce qu'il faut pour fabriquer une vidéo d'habillage.</summary>
public sealed record AtelierJob(
    AtelierKind Kind,
    string Title,
    string? Subtitle,
    string? Label,
    Palette Palette,
    string? SoundPath,
    RhythmAnalysis Rhythm,
    string? LogoPath,
    bool LogoOnBlack,
    int Width,
    int Height,
    FrameRate Rate,
    double Duration,
    string? SourcePath,
    double[] ExtractStarts,
    int Seed,
    string OutputPath);

/// <summary>
/// Moteur de rendu « Rétro 95 » : une mosaïque de rectangles de couleur, séparés par de fins
/// liserés vert amande, qui se redécoupe et change de couleurs sur les temps de la musique
/// (temps forts = grands mouvements, attaques = éclairs). Par-dessus : étiquette, bandeau titre
/// noir en capitales italiques, bloc d'horaire, cartouche du logo, et pour les bandes-annonces
/// une fenêtre d'extraits coupés sur les temps forts. Les images sont dessinées avec SkiaSharp
/// et encodées par FFmpeg avec le son.
/// </summary>
public sealed class Retro95Renderer(Microsoft.Extensions.Options.IOptions<NarOptions> options, ILogger<Retro95Renderer> logger)
{
    private static readonly string FontDir = Path.Combine(AppContext.BaseDirectory, "Resources", "Fonts");
    private static readonly Lazy<SKTypeface> TitleFace = new(() => LoadFace("BarlowCondensed-ExtraBoldItalic.ttf", SKFontStyleWeight.ExtraBold, SKFontStyleSlant.Italic));
    private static readonly Lazy<SKTypeface> BlockFace = new(() => LoadFace("BarlowCondensed-BoldItalic.ttf", SKFontStyleWeight.Bold, SKFontStyleSlant.Italic));
    private static readonly Lazy<SKTypeface> LabelFace = new(() => LoadFace("BarlowCondensed-SemiBold.ttf", SKFontStyleWeight.SemiBold, SKFontStyleSlant.Upright));

    private static SKTypeface LoadFace(string file, SKFontStyleWeight weight, SKFontStyleSlant slant)
    {
        var path = Path.Combine(FontDir, file);
        return File.Exists(path) ? SKTypeface.FromFile(path) : SKTypeface.FromFamilyName("sans-serif", weight, SKFontStyleWidth.Condensed, slant);
    }

    public async Task RenderAsync(AtelierJob job, IProgress<double>? progress, CancellationToken ct)
    {
        var w = job.Width;
        var h = job.Height;
        var rate = job.Rate;
        var totalFrames = (int)Math.Ceiling(job.Duration * rate.FramesPerSecond);
        Directory.CreateDirectory(Path.GetDirectoryName(job.OutputPath)!);

        using var logo = job.LogoPath is not null && File.Exists(job.LogoPath) ? SKBitmap.Decode(job.LogoPath) : null;
        var scene = new Scene(job, logo);

        using var encoder = StartEncoder(job);
        var stdin = encoder.StandardInput.BaseStream;
        var stderr = encoder.StandardError.ReadToEndAsync(ct);

        using var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        var frameBuffer = new byte[w * h * 4];
        using var extracts = job.Kind == AtelierKind.BandeAnnonce && job.SourcePath is not null
            ? new ExtractPlayer(options.Value.FfmpegPath, job.SourcePath, job.ExtractStarts, scene.WindowSize, rate)
            : null;

        try
        {
            for (var f = 0; f < totalFrames; f++)
            {
                ct.ThrowIfCancellationRequested();
                var t = rate.TimeFromFrames(f).TotalSeconds;
                SKBitmap? video = null;
                if (extracts is not null && scene.ExtractIndexAt(t) is var (index, startedAt) && index >= 0)
                    video = extracts.FrameAt(index, t - startedAt);
                scene.Draw(canvas, t, video);
                canvas.Flush();
                System.Runtime.InteropServices.Marshal.Copy(bitmap.GetPixels(), frameBuffer, 0, frameBuffer.Length);
                await stdin.WriteAsync(frameBuffer, ct);
                if (f % 25 == 0) progress?.Report((double)f / totalFrames);
            }
            stdin.Close();
            await encoder.WaitForExitAsync(ct);
            if (encoder.ExitCode != 0) throw new InvalidOperationException($"Encodage impossible : {(await stderr).Trim().Split('\n').LastOrDefault()}");
            progress?.Report(1);
        }
        catch
        {
            try { if (!encoder.HasExited) encoder.Kill(true); } catch { }
            throw;
        }
    }

    private Process StartEncoder(AtelierJob job)
    {
        var ci = CultureInfo.InvariantCulture;
        var psi = new ProcessStartInfo(options.Value.FfmpegPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
        };
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "rawvideo", "-pix_fmt", "rgba", "-s", $"{job.Width}x{job.Height}",
            "-framerate", $"{job.Rate.Numerator}/{job.Rate.Denominator}", "-i", "pipe:0",
        };
        var duration = job.Duration.ToString("0.###", ci);
        var fadeStart = Math.Max(0, job.Duration - 0.4).ToString("0.###", ci);
        if (job.SoundPath is not null)
            args.AddRange(["-i", job.SoundPath, "-map", "0:v", "-map", "1:a", "-af", $"atrim=0:{duration},afade=t=out:st={fadeStart}:d=0.4,aresample=48000"]);
        else
            args.AddRange(["-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo", "-map", "0:v", "-map", "1:a"]);
        args.AddRange(["-t", duration, "-c:v", "libx264", "-preset", "medium", "-crf", "17", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "192k", "-ac", "2", "-movflags", "+faststart", job.OutputPath]);
        foreach (var a in args) psi.ArgumentList.Add(a);
        var process = Process.Start(psi) ?? throw new InvalidOperationException("FFmpeg n'a pas démarré");
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        logger.LogDebug("Rendu {Kind} → {Output}", job.Kind, job.OutputPath);
        return process;
    }

    // ---------------------------------------------------------------------------------------
    // Scène : mosaïque animée + éléments d'habillage, calculés pour un instant t (en secondes).
    // ---------------------------------------------------------------------------------------
    private sealed class Scene
    {
        private readonly AtelierJob _job;
        private readonly SKBitmap? _logo;
        private readonly Random _random;
        private readonly Node _root;
        private readonly List<Node> _leaves = new();
        private readonly List<Node> _splits = new();
        private readonly double[] _beats;
        private readonly double _period;
        private int _nextBeat;
        private int _nextAccent;
        private readonly float _w, _h, _margin, _gap;
        private readonly SKRect _inner;

        // Repères de la chorégraphie (en secondes), calés sur les temps.
        private readonly double _labelAt, _titleAt, _subtitleAt, _logoAt, _windowOpenAt, _windowCloseAt;
        private readonly List<double> _cuts = new();

        // Après le dernier coup d'une fin qui résonne, la mosaïque se fige avec le son.
        private readonly double _freezeAfter = double.MaxValue;

        public Scene(AtelierJob job, SKBitmap? logo)
        {
            _job = job;
            _logo = logo;
            _random = new Random(job.Seed);
            _w = job.Width;
            _h = job.Height;
            _margin = _h * 0.045f;
            _gap = Math.Max(2, _h * 0.008f);
            _inner = new SKRect(_margin, _margin, _w - _margin, _h - _margin);

            _beats = job.Rhythm.Beats.Where(b => b < job.Duration).ToArray();
            if (_beats.Length < 2) _beats = RhythmAnalysis.Grid(job.Duration).Beats;
            _period = _beats.Length > 1 ? (_beats[^1] - _beats[0]) / (_beats.Length - 1) : 0.5;

            _root = Build(4, vertical: _random.Next(2) == 0, depth: 0);
            foreach (var leaf in _leaves) leaf.Color = leaf.Target = job.Palette.Pick(_random);

            // Chorégraphie : on s'accroche aux temps forts quand c'est possible.
            var downbeats = _beats.Where((_, i) => job.Rhythm.IsDownbeat(i)).ToList();
            double BeatAfter(double t, bool strong = true)
            {
                var list = strong && downbeats.Count > 0 ? downbeats : _beats.ToList();
                return list.FirstOrDefault(b => b >= t - 0.01, Math.Min(t, job.Duration));
            }
            var d = job.Duration;
            _labelAt = BeatAfter(d * 0.12);
            _titleAt = BeatAfter(Math.Max(_labelAt + _period * 1.5, d * 0.22), strong: false);
            _subtitleAt = BeatAfter(_titleAt + _period * 1.5, strong: false);
            _logoAt = job.Kind == AtelierKind.Ident ? BeatAfter(d * 0.08, strong: false) : BeatAfter(Math.Max(d - Math.Max(1.6, _period * 3), d * 0.55), strong: false);

            if (job.Kind == AtelierKind.Jingle)
            {
                // Jingle : le titre arrive vers la fin, selon la façon dont le son se termine.
                //  - fin qui résonne : le titre tombe sur le dernier coup et reste pendant la résonance ;
                //  - fin nette : le titre arrive assez tôt pour être lu avant la coupure, sur un temps fort.
                var ending = job.Rhythm.Ending(d);
                var read = Math.Min(Math.Max(1.8, _period * 3), d * 0.45);
                double BeatBefore(double t, bool strong)
                {
                    var list = strong && downbeats.Count > 0 ? downbeats : _beats.ToList();
                    return list.LastOrDefault(b => b <= t + 0.01, double.NaN);
                }
                double at;
                if (ending.Resonates) _freezeAfter = ending.FinalHit + 0.05;
                if (ending.Resonates && d - ending.FinalHit >= read) at = ending.FinalHit;
                else
                {
                    var latest = Math.Min(ending.MusicEnd, d) - read;
                    // Temps fort si l'on n'avance pas de plus d'une mesure, sinon le temps le plus proche.
                    var strongBeat = BeatBefore(latest, strong: true);
                    at = !double.IsNaN(strongBeat) && latest - strongBeat <= _period * 4.1 ? strongBeat : BeatBefore(latest, strong: false);
                    if (double.IsNaN(at)) at = latest;
                }
                _titleAt = Math.Clamp(at, Math.Min(_labelAt + _period, d * 0.5), Math.Max(d - 1.0, 0));
                var left = d - _titleAt;
                _subtitleAt = left > _period * 2 + 0.6 ? _titleAt + _period : _titleAt + Math.Min(0.25, left / 4);
                _logoAt = Math.Min(Math.Max(_titleAt + _period * 2, _logoAt), Math.Max(d - 0.7, _titleAt));
            }

            if (job.Kind == AtelierKind.BandeAnnonce)
            {
                _windowOpenAt = BeatAfter(Math.Min(0.6, d * 0.05), strong: false);
                _windowCloseAt = BeatAfter(Math.Max(d - Math.Max(3.0, _period * 6), d * 0.6));
                // Coupes : tous les temps forts (ou tous les 2 temps si le tempo est lent).
                var step = _period > 0.75 ? 2 : 4;
                for (var i = 0; i < _beats.Length; i++)
                {
                    if (_beats[i] < _windowOpenAt || _beats[i] >= _windowCloseAt) continue;
                    if (_cuts.Count == 0 || (i - Array.IndexOf(_beats, _cuts[^1])) >= step) _cuts.Add(_beats[i]);
                }
                if (_cuts.Count == 0) _cuts.Add(_windowOpenAt);
                // Le titre arrive avec la fermeture de la fenêtre.
                _titleAt = _windowCloseAt + _period * 0.5;
                _subtitleAt = _titleAt + _period;
                _labelAt = _windowOpenAt + _period;
                _logoAt = _titleAt + _period * 2;
            }
        }

        public SKSize WindowSize => new((float)Math.Round(_w * 0.58 / 2) * 2, (float)Math.Round(_h * 0.62 / 2) * 2);

        public (int Index, double StartedAt) ExtractIndexAt(double t)
        {
            if (t < _windowOpenAt || t >= _windowCloseAt) return (-1, 0);
            var i = _cuts.FindLastIndex(c => c <= t);
            return i < 0 ? (0, _windowOpenAt) : (i, _cuts[i]);
        }

        // --- Mosaïque (arbre de découpe binaire) ------------------------------------------
        private sealed class Node
        {
            public bool Vertical;
            public double From = 0.5, To = 0.5, AnimStart, AnimEnd;
            public Node? A, B;
            public SKColor Color, Target;
            public double WipeStart = -1, WipeEnd;
            public int WipeDirection;
            public double FlashAt = -10;
            public SKRect Rect;
            public bool IsLeaf => A is null;

            public double Ratio(double t)
            {
                if (t >= AnimEnd) return To;
                if (t <= AnimStart) return From;
                var x = (t - AnimStart) / (AnimEnd - AnimStart);
                var eased = 1 - Math.Pow(1 - x, 3);
                return From + (To - From) * eased;
            }
        }

        private Node Build(int maxDepth, bool vertical, int depth)
        {
            var node = new Node { Vertical = vertical };
            var stop = depth >= maxDepth || (depth >= 2 && _random.NextDouble() < 0.28);
            if (stop)
            {
                _leaves.Add(node);
                return node;
            }
            node.From = node.To = 0.32 + _random.NextDouble() * 0.36;
            node.A = Build(maxDepth, _random.NextDouble() < 0.7 ? !vertical : vertical, depth + 1);
            node.B = Build(maxDepth, _random.NextDouble() < 0.7 ? !vertical : vertical, depth + 1);
            _splits.Add(node);
            return node;
        }

        private void Layout(Node node, SKRect rect, double t)
        {
            node.Rect = rect;
            if (node.IsLeaf) return;
            var r = (float)node.Ratio(t);
            if (node.Vertical)
            {
                var x = rect.Left + rect.Width * r;
                Layout(node.A!, new SKRect(rect.Left, rect.Top, x, rect.Bottom), t);
                Layout(node.B!, new SKRect(x, rect.Top, rect.Right, rect.Bottom), t);
            }
            else
            {
                var y = rect.Top + rect.Height * r;
                Layout(node.A!, new SKRect(rect.Left, rect.Top, rect.Right, y), t);
                Layout(node.B!, new SKRect(rect.Left, y, rect.Right, rect.Bottom), t);
            }
        }

        /// <summary>Applique les événements musicaux arrivés depuis l'image précédente.</summary>
        private void Advance(double t)
        {
            while (_nextBeat < _beats.Length && _beats[_nextBeat] <= t)
            {
                var at = _beats[_nextBeat];
                if (at > _freezeAfter) { _nextBeat++; continue; }
                var strength = _nextBeat < _job.Rhythm.BeatStrengths.Length ? _job.Rhythm.BeatStrengths[_nextBeat] : 0.6;
                var downbeat = _job.Rhythm.IsDownbeat(_nextBeat);
                var energy = EnergyAt(at);

                // Couleurs : plus la musique est forte, plus il y a de cases qui changent.
                var recolor = 1 + (int)Math.Round(strength * 2 + energy * 2) + (downbeat ? 2 : 0);
                foreach (var leaf in _leaves.OrderBy(_ => _random.Next()).Take(recolor))
                {
                    var next = _job.Palette.Pick(_random);
                    for (var tries = 0; tries < 4 && next == leaf.Color; tries++) next = _job.Palette.Pick(_random);
                    leaf.Color = leaf.WipeStart >= 0 && at < leaf.WipeEnd ? leaf.Target : leaf.Color;
                    leaf.Target = next;
                    leaf.WipeStart = at;
                    leaf.WipeEnd = at + Math.Min(0.14, _period * 0.35);
                    leaf.WipeDirection = _random.Next(4);
                }

                // Découpe : les temps forts redessinent la mosaïque, les autres la font respirer.
                var moves = downbeat ? 2 + _random.Next(2) : (_random.NextDouble() < strength ? 1 : 0);
                foreach (var split in _splits.OrderBy(_ => _random.Next()).Take(moves))
                {
                    split.From = split.Ratio(at);
                    split.To = 0.25 + _random.NextDouble() * 0.5;
                    split.AnimStart = at;
                    split.AnimEnd = at + _period * (downbeat ? 0.45 : 0.3);
                }
                _nextBeat++;
            }
            while (_nextAccent < _job.Rhythm.Accents.Length && _job.Rhythm.Accents[_nextAccent] <= t)
            {
                if (_job.Rhythm.Accents[_nextAccent] <= _freezeAfter)
                    _leaves[_random.Next(_leaves.Count)].FlashAt = _job.Rhythm.Accents[_nextAccent];
                _nextAccent++;
            }
        }

        private double EnergyAt(double t)
        {
            var e = _job.Rhythm.Energy;
            if (e.Length == 0) return 0.6;
            var i = (int)(t / _job.Rhythm.EnergyStep);
            return e[Math.Clamp(i, 0, e.Length - 1)];
        }

        public void Draw(SKCanvas canvas, double t, SKBitmap? video)
        {
            Advance(t);
            canvas.Clear(_job.Palette.Frame);
            Layout(_root, _inner, t);

            using var fill = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill };
            foreach (var leaf in _leaves)
            {
                var r = Inset(leaf.Rect, _gap / 2);
                if (r.Width <= 1 || r.Height <= 1) continue;
                fill.Color = leaf.Color;
                canvas.DrawRect(r, fill);
                if (leaf.WipeStart >= 0)
                {
                    var p = (float)Math.Clamp((t - leaf.WipeStart) / Math.Max(0.001, leaf.WipeEnd - leaf.WipeStart), 0, 1);
                    if (p >= 1) { leaf.Color = leaf.Target; leaf.WipeStart = -1; }
                    fill.Color = leaf.Target;
                    var wipe = leaf.WipeDirection switch
                    {
                        0 => new SKRect(r.Left, r.Top, r.Left + r.Width * p, r.Bottom),
                        1 => new SKRect(r.Right - r.Width * p, r.Top, r.Right, r.Bottom),
                        2 => new SKRect(r.Left, r.Top, r.Right, r.Top + r.Height * p),
                        _ => new SKRect(r.Left, r.Bottom - r.Height * p, r.Right, r.Bottom),
                    };
                    canvas.DrawRect(p >= 1 ? r : wipe, fill);
                }
                // Éclair franc (2 à 3 images en blanc), comme un coup de projecteur.
                var flash = t - leaf.FlashAt;
                if (flash is >= 0 and < 0.09)
                {
                    fill.Color = Palettes.Blanc;
                    canvas.DrawRect(r, fill);
                }
            }

            if (_job.Kind == AtelierKind.BandeAnnonce) DrawWindow(canvas, t, video);
            DrawOverlays(canvas, t);
        }

        // --- Fenêtre d'extraits (bande-annonce) -----------------------------------------
        private SKRect WindowRect()
        {
            var size = WindowSize;
            var x = _inner.Left + _inner.Width * 0.06f;
            var y = _inner.Top + _inner.Height * 0.08f;
            return new SKRect(x, y, x + size.Width, y + size.Height);
        }

        private void DrawWindow(SKCanvas canvas, double t, SKBitmap? video)
        {
            if (t < _windowOpenAt) return;
            var full = WindowRect();
            var open = Ease(Math.Clamp((t - _windowOpenAt) / 0.25, 0, 1));
            var close = t >= _windowCloseAt ? Ease(Math.Clamp((t - _windowCloseAt) / 0.25, 0, 1)) : 0;
            var k = (float)(open * (1 - close));
            if (k <= 0) return;

            // Ouverture « en volet » depuis la gauche, cadre vert amande épais.
            var rect = new SKRect(full.Left, full.Top, full.Left + full.Width * k, full.Bottom);
            using var paint = new SKPaint { Style = SKPaintStyle.Fill, Color = _job.Palette.Frame };
            canvas.DrawRect(Inset(rect, -_gap * 1.5f), paint);
            paint.Color = Palettes.Noir;
            canvas.DrawRect(rect, paint);
            if (video is not null)
            {
                canvas.Save();
                canvas.ClipRect(rect);
                canvas.DrawBitmap(video, full);
                canvas.Restore();
            }
            // Petit éclair blanc à chaque coupe.
            foreach (var cut in _cuts)
            {
                var dt = t - cut;
                if (dt is < 0 or > 0.08 || cut == _cuts[0]) continue;
                paint.Color = Palettes.Blanc.WithAlpha((byte)(200 * (1 - dt / 0.08)));
                canvas.DrawRect(rect, paint);
            }
        }

        // --- Étiquette, titre, horaire, logo --------------------------------------------
        private void DrawOverlays(SKCanvas canvas, double t)
        {
            var title = (_job.Title ?? "").Trim().ToUpperInvariant();
            var subtitle = _job.Subtitle?.Trim().ToUpperInvariant();
            var label = _job.Label?.Trim().ToUpperInvariant();
            var big = _job.Kind is AtelierKind.Generique;

            // Étiquette (CINÉMA, SÉRIE…) en haut à gauche.
            var labelGone = _job.Kind == AtelierKind.BandeAnnonce && t >= _windowCloseAt;
            if (!string.IsNullOrEmpty(label) && t >= _labelAt && !labelGone)
            {
                var pop = Ease(Math.Clamp((t - _labelAt) / 0.15, 0, 1));
                using var text = TextPaint(LabelFace.Value, _h * 0.055f, Palettes.Noir);
                var pad = _h * 0.025f;
                var width = text.MeasureText(label) + pad * 2;
                var height = _h * 0.085f;
                var x = _inner.Left + _inner.Width * 0.06f;
                var y = _inner.Top + _inner.Height * (_job.Kind == AtelierKind.BandeAnnonce ? 0.74f : 0.12f);
                var rect = new SKRect(x, y, x + width * (float)pop, y + height);
                using var bg = new SKPaint { Color = Palettes.Blanc, Style = SKPaintStyle.Fill };
                using var edge = new SKPaint { Color = Palettes.Noir, Style = SKPaintStyle.Fill };
                canvas.DrawRect(Inset(rect, -_gap * 0.8f), edge);
                canvas.DrawRect(rect, bg);
                canvas.Save();
                canvas.ClipRect(rect);
                canvas.DrawText(label, x + pad, y + height * 0.72f, text);
                canvas.Restore();
            }

            // Bandeau titre noir, capitales italiques, qui glisse depuis la gauche.
            if (!string.IsNullOrEmpty(title) && t >= _titleAt && (_job.Kind != AtelierKind.Ident))
            {
                var slide = Ease(Math.Clamp((t - _titleAt) / 0.2, 0, 1));
                var size = _h * (big ? 0.12f : 0.095f);
                using var text = TextPaint(TitleFace.Value, size, Palettes.Blanc);
                // Titre trop long : on réduit la taille pour tenir dans la largeur.
                var maxWidth = _inner.Width * 0.82f;
                var measured = text.MeasureText(title);
                if (measured > maxWidth - size)
                {
                    text.TextSize = size * (maxWidth - size) / measured;
                    measured = text.MeasureText(title);
                }
                var pad = size * 0.45f;
                var height = text.TextSize * 1.32f;
                var x = _inner.Left + _inner.Width * 0.06f;
                var y = _job.Kind == AtelierKind.BandeAnnonce ? _inner.Top + _inner.Height * 0.38f : _inner.Top + _inner.Height * (big ? 0.38f : 0.42f);
                var full = measured + pad * 2;
                var rect = new SKRect(x, y, x + full * (float)slide, y + height);
                using var bg = new SKPaint { Color = Palettes.Noir, Style = SKPaintStyle.Fill };
                using var liseré = new SKPaint { Color = _job.Palette.Frame, Style = SKPaintStyle.Fill };
                canvas.DrawRect(Inset(rect, -_gap), liseré);
                canvas.DrawRect(rect, bg);
                canvas.Save();
                canvas.ClipRect(rect);
                canvas.DrawText(title, x + pad, y + height * 0.76f, text);
                canvas.Restore();

                // Bloc d'horaire (CE SOIR · 20H50) sous le titre.
                if (!string.IsNullOrEmpty(subtitle) && t >= _subtitleAt)
                {
                    var pop = Ease(Math.Clamp((t - _subtitleAt) / 0.15, 0, 1));
                    using var subText = TextPaint(BlockFace.Value, _h * 0.06f, _job.Palette.LabelText);
                    var subPad = _h * 0.022f;
                    var subW = subText.MeasureText(subtitle) + subPad * 2;
                    var subH = _h * 0.085f;
                    var sx = x + pad * 0.6f;
                    var sy = rect.Bottom + _gap * 1.5f;
                    var subRect = new SKRect(sx, sy, sx + subW, sy + subH * (float)pop);
                    using var subBg = new SKPaint { Color = _job.Palette.Label, Style = SKPaintStyle.Fill };
                    using var subEdge = new SKPaint { Color = Palettes.Noir, Style = SKPaintStyle.Fill };
                    canvas.DrawRect(Inset(subRect, -_gap * 0.8f), subEdge);
                    canvas.DrawRect(subRect, subBg);
                    canvas.Save();
                    canvas.ClipRect(subRect);
                    canvas.DrawText(subtitle, sx + subPad, sy + subH * 0.72f, subText);
                    canvas.Restore();
                }
            }

            // Cartouche du logo de la chaîne.
            if (t >= _logoAt)
            {
                var grow = Ease(Math.Clamp((t - _logoAt) / 0.25, 0, 1));
                SKRect target;
                if (_job.Kind == AtelierKind.Ident)
                {
                    var bw = _inner.Width * 0.34f;
                    var bh = _inner.Height * 0.36f;
                    target = new SKRect(_w / 2 - bw / 2, _h / 2 - bh / 2, _w / 2 + bw / 2, _h / 2 + bh / 2);
                }
                else
                {
                    var bw = _inner.Width * 0.2f;
                    var bh = _inner.Height * 0.22f;
                    target = new SKRect(_inner.Right - _inner.Width * 0.05f - bw, _inner.Bottom - _inner.Height * 0.07f - bh,
                        _inner.Right - _inner.Width * 0.05f, _inner.Bottom - _inner.Height * 0.07f);
                }
                var rect = new SKRect(target.Left, target.Bottom - target.Height * (float)grow, target.Right, target.Bottom);
                using var bg = new SKPaint { Color = _job.LogoOnBlack ? Palettes.Noir : Palettes.Blanc, Style = SKPaintStyle.Fill };
                using var frame = new SKPaint { Color = _job.Palette.Frame, Style = SKPaintStyle.Fill };
                canvas.DrawRect(Inset(rect, -_gap * 1.2f), frame);
                canvas.DrawRect(rect, bg);
                if (_logo is not null && grow > 0.6)
                {
                    var inner = Inset(target, target.Height * 0.12f);
                    var scale = Math.Min(inner.Width / _logo.Width, inner.Height / _logo.Height);
                    var lw = _logo.Width * scale;
                    var lh = _logo.Height * scale;
                    var dest = new SKRect(inner.MidX - lw / 2, inner.MidY - lh / 2, inner.MidX + lw / 2, inner.MidY + lh / 2);
                    using var img = new SKPaint { FilterQuality = SKFilterQuality.High, Color = SKColors.White.WithAlpha((byte)(255 * Math.Clamp((grow - 0.6) / 0.4, 0, 1))) };
                    canvas.DrawBitmap(_logo, dest, img);
                }
                else if (_logo is null && grow > 0.6)
                {
                    // Pas de logo envoyé : le nom de l'étiquette ou « MA CHAÎNE ».
                    using var text = TextPaint(TitleFace.Value, target.Height * 0.3f, _job.LogoOnBlack ? Palettes.Blanc : Palettes.Noir);
                    const string name = "MA CHAÎNE";
                    var tw = text.MeasureText(name);
                    if (tw > target.Width * 0.85f) text.TextSize *= target.Width * 0.85f / tw;
                    tw = text.MeasureText(name);
                    canvas.DrawText(name, target.MidX - tw / 2, target.MidY + text.TextSize * 0.35f, text);
                }
            }
        }

        private static SKPaint TextPaint(SKTypeface face, float size, SKColor color) =>
            new() { Typeface = face, TextSize = size, Color = color, IsAntialias = true, SubpixelText = true };

        private static SKRect Inset(SKRect r, float d) => new(r.Left + d, r.Top + d, r.Right - d, r.Bottom - d);

        private static double Ease(double x) => 1 - Math.Pow(1 - x, 3);
    }

    // ---------------------------------------------------------------------------------------
    // Extraits d'un programme décodés à la taille de la fenêtre (un FFmpeg par extrait).
    // ---------------------------------------------------------------------------------------
    private sealed class ExtractPlayer(string ffmpeg, string source, double[] starts, SKSize size, FrameRate rate) : IDisposable
    {
        private readonly int _w = (int)size.Width;
        private readonly int _h = (int)size.Height;
        private int _current = -1;
        private Process? _process;
        private SKBitmap? _frame;
        private double _shown = -1;

        public SKBitmap? FrameAt(int index, double elapsed)
        {
            if (starts.Length == 0) return null;
            if (index != _current)
            {
                Stop();
                _current = index;
                _shown = -1;
                var start = starts[index % starts.Length];
                var psi = new ProcessStartInfo(ffmpeg)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                foreach (var a in new[]
                         {
                             "-hide_banner", "-loglevel", "error", "-nostdin",
                             "-ss", start.ToString("0.000", CultureInfo.InvariantCulture), "-i", source, "-an",
                             "-vf", $"scale={_w}:{_h}:force_original_aspect_ratio=increase,crop={_w}:{_h},fps={rate.Numerator}/{rate.Denominator},format=rgba",
                             "-f", "rawvideo", "pipe:1",
                         })
                    psi.ArgumentList.Add(a);
                _process = Process.Start(psi);
                _ = _process?.StandardError.ReadToEndAsync();
                _frame ??= new SKBitmap(new SKImageInfo(_w, _h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            }

            // Une image décodée par image produite (même cadence).
            var wanted = Math.Floor(elapsed * rate.FramesPerSecond);
            while (_shown < wanted && _process is not null)
            {
                if (!ReadFrame()) break;
                _shown++;
            }
            return _shown >= 0 ? _frame : null;
        }

        private bool ReadFrame()
        {
            var bytes = _w * _h * 4;
            var buffer = new byte[bytes];
            var stream = _process!.StandardOutput.BaseStream;
            var read = 0;
            while (read < bytes)
            {
                var n = stream.Read(buffer, read, bytes - read);
                if (n <= 0) return false;
                read += n;
            }
            System.Runtime.InteropServices.Marshal.Copy(buffer, 0, _frame!.GetPixels(), bytes);
            _frame.NotifyPixelsChanged();
            return true;
        }

        private void Stop()
        {
            try { if (_process is { HasExited: false }) _process.Kill(true); } catch { }
            _process?.Dispose();
            _process = null;
        }

        public void Dispose()
        {
            Stop();
            _frame?.Dispose();
        }
    }
}
