using System.Diagnostics;
using System.Globalization;

namespace NAR.Server.Services.Atelier;

/// <summary>
/// Résultat de l'analyse rythmique d'un son : tempo, temps (battements), temps forts
/// (début de mesure), attaques marquées et énergie. Toutes les positions sont en secondes.
/// </summary>
public sealed record RhythmAnalysis(
    double Duration,
    double Bpm,
    double[] Beats,
    double[] BeatStrengths,
    int DownbeatPhase,
    double[] Accents,
    double EnergyStep,
    double[] Energy)
{
    public bool IsDownbeat(int beatIndex) => beatIndex % 4 == DownbeatPhase;

    /// <summary>
    /// Comment le son se termine avant <paramref name="until"/> (la durée de la création) :
    /// fin de la musique proprement dite (dernier moment où l'énergie reste à moins de
    /// 12 dB du maximum), longueur de la queue (résonance, réverbération) et dernier coup
    /// marqué. Une queue courte = fin nette ; une queue longue = fin qui résonne.
    /// </summary>
    public SoundEnding Ending(double until)
    {
        until = Math.Min(until, Duration);
        if (Energy.Length == 0 || EnergyStep <= 0) return new SoundEnding(until, 0, Beats.LastOrDefault(b => b < until, until));
        var count = Math.Min(Energy.Length, (int)Math.Ceiling(until / EnergyStep));
        if (count < 3) return new SoundEnding(until, 0, until);
        // Lissage sur 3 cases (0,3 s) pour ne pas confondre un silence entre deux coups avec la fin.
        var smooth = new double[count];
        for (var i = 0; i < count; i++)
        {
            double sum = 0; var n = 0;
            for (var j = Math.Max(0, i - 1); j <= Math.Min(count - 1, i + 1); j++) { sum += Energy[j]; n++; }
            smooth[i] = sum / n;
        }
        var peak = smooth.Max();
        if (peak <= 1e-6) return new SoundEnding(until, 0, until);
        var last = count - 1;
        while (last > 0 && smooth[last] < peak * 0.25) last--;
        var musicEnd = Math.Min(until, (last + 1) * EnergyStep);
        // Dernier coup : la dernière attaque marquée (sinon le dernier temps) avant la fin de la musique.
        var hit = Accents.Where(a => a <= musicEnd + 0.15 && a >= musicEnd - 2.5).DefaultIfEmpty(double.NaN).Max();
        if (double.IsNaN(hit)) hit = Beats.Where(b => b <= musicEnd + 0.1).DefaultIfEmpty(musicEnd).Max();
        return new SoundEnding(musicEnd, until - musicEnd, Math.Min(hit, until));
    }

    /// <summary>Grille régulière quand il n'y a pas de son (ou qu'aucun rythme n'est trouvé).</summary>
    public static RhythmAnalysis Grid(double duration, double bpm = 120)
    {
        var period = 60.0 / bpm;
        var beats = Enumerable.Range(0, (int)(duration / period) + 1).Select(i => i * period).Where(t => t < duration).ToArray();
        return new RhythmAnalysis(duration, bpm, beats, beats.Select(_ => 0.7).ToArray(), 0, [], 0.1,
            Enumerable.Repeat(0.6, (int)(duration / 0.1) + 1).ToArray());
    }
}

/// <param name="MusicEnd">Fin de la musique (en s), avant la queue.</param>
/// <param name="Tail">Durée de la queue après la musique : résonance, réverbération, silence.</param>
/// <param name="FinalHit">Dernier coup marqué (en s).</param>
public sealed record SoundEnding(double MusicEnd, double Tail, double FinalHit)
{
    /// <summary>Au-delà de 1,2 s de queue, on considère que le son « résonne » plutôt qu'il ne s'arrête net.</summary>
    public bool Resonates => Tail >= 1.2;

    public string Label => Resonates ? $"fin qui résonne ({Tail:0.0} s)".Replace('.', ',') : "fin nette";
}

/// <summary>
/// Analyse rythmique maison (sans dépendance) : flux spectral → enveloppe d'attaques,
/// tempo par autocorrélation pondérée autour de 120 BPM, puis suivi des temps par
/// programmation dynamique (méthode d'Ellis, celle de librosa).
/// </summary>
public sealed class AudioAnalyzer(Microsoft.Extensions.Options.IOptions<NarOptions> options)
{
    private const int SampleRate = 22050;
    private const int FftSize = 1024;
    private const int Hop = 256;
    private const double FrameRate = (double)SampleRate / Hop; // ≈ 86 images d'analyse / s

    /// <summary>Une image d'analyse est datée au centre de sa fenêtre.</summary>
    private const double Latency = FftSize / 2.0 / SampleRate;

    public async Task<RhythmAnalysis> AnalyzeAsync(string path, CancellationToken ct)
    {
        var samples = await DecodeAsync(path, ct);
        if (samples.Length < SampleRate / 2) throw new InvalidDataException("Son trop court ou illisible.");
        return Analyze(samples);
    }

    public static RhythmAnalysis Analyze(float[] samples)
    {
        var duration = (double)samples.Length / SampleRate;
        var onset = OnsetEnvelope(samples);
        var energy = EnergyEnvelope(samples, 0.1);

        if (onset.Length < 32 || onset.Max() <= 1e-9) return RhythmAnalysis.Grid(duration) with { Energy = energy };

        var period = EstimatePeriod(onset);
        var beatFrames = TrackBeats(onset, period);
        if (beatFrames.Count < 2) return RhythmAnalysis.Grid(duration, 60 * FrameRate / period) with { Energy = energy };

        var max = onset.Max();
        var beats = beatFrames.Select(f => f / FrameRate + Latency).ToArray();
        var strengths = beatFrames.Select(f => Math.Clamp(Peak(onset, f, 2) / max, 0, 1)).ToArray();

        // Début de mesure (4/4) : la phase dont les temps sont en moyenne les plus forts.
        var phase = Enumerable.Range(0, 4)
            .Select(p => (p, s: strengths.Where((_, i) => i % 4 == p).DefaultIfEmpty(0).Average()))
            .MaxBy(x => x.s).p;

        // Attaques marquées (caisse claire, coup d'orchestre…) : pics nettement au-dessus de la moyenne.
        var mean = onset.Average();
        var std = Math.Sqrt(onset.Select(v => (v - mean) * (v - mean)).Average());
        var accents = new List<double>();
        for (var i = 2; i < onset.Length - 2; i++)
        {
            if (onset[i] > mean + 2.2 * std && onset[i] >= onset[i - 1] && onset[i] >= onset[i + 1] && onset[i] >= onset[i - 2] && onset[i] >= onset[i + 2])
            {
                var t = i / FrameRate + Latency;
                if (accents.Count == 0 || t - accents[^1] > 0.12) accents.Add(t);
            }
        }

        // Le BPM final vient de l'écart médian réel entre temps détectés.
        var gaps = beats.Zip(beats.Skip(1), (a, b) => b - a).OrderBy(x => x).ToArray();
        var bpm = 60.0 / gaps[gaps.Length / 2];
        return new RhythmAnalysis(duration, Math.Round(bpm, 1), beats, strengths, phase, accents.ToArray(), 0.1, energy);
    }

    private static double Peak(double[] env, int center, int radius)
    {
        var best = 0.0;
        for (var i = Math.Max(0, center - radius); i <= Math.Min(env.Length - 1, center + radius); i++) best = Math.Max(best, env[i]);
        return best;
    }

    /// <summary>Flux spectral (différence positive des spectres log successifs), centré et normalisé.</summary>
    private static double[] OnsetEnvelope(float[] x)
    {
        var frames = (x.Length - FftSize) / Hop;
        if (frames < 4) return [];
        var window = Enumerable.Range(0, FftSize).Select(n => 0.5 - 0.5 * Math.Cos(2 * Math.PI * n / FftSize)).ToArray();
        var bins = FftSize / 2;
        var maxBin = (int)(8000.0 / SampleRate * FftSize); // au-delà de 8 kHz : peu utile pour le rythme
        var prev = new double[bins];
        var cur = new double[bins];
        var re = new double[FftSize];
        var im = new double[FftSize];
        var env = new double[frames];

        for (var f = 0; f < frames; f++)
        {
            var offset = f * Hop;
            for (var n = 0; n < FftSize; n++)
            {
                re[n] = x[offset + n] * window[n];
                im[n] = 0;
            }
            Fft(re, im);
            double flux = 0;
            for (var k = 1; k < maxBin; k++)
            {
                cur[k] = Math.Log(1 + 100 * Math.Sqrt(re[k] * re[k] + im[k] * im[k]));
                var d = cur[k] - prev[k];
                if (d > 0) flux += d;
            }
            env[f] = f == 0 ? 0 : flux;
            (prev, cur) = (cur, prev);
        }

        // Retire la tendance lente (moyenne glissante ~0,4 s) et ne garde que le positif.
        var w = (int)(0.4 * FrameRate);
        var result = new double[frames];
        double sum = 0;
        var queue = new Queue<double>();
        for (var i = 0; i < frames; i++)
        {
            queue.Enqueue(env[i]);
            sum += env[i];
            if (queue.Count > w) sum -= queue.Dequeue();
            result[i] = Math.Max(0, env[i] - sum / queue.Count);
        }
        var std = Math.Sqrt(result.Select(v => v * v).Average());
        if (std > 0) for (var i = 0; i < frames; i++) result[i] /= std;
        return result;
    }

    /// <summary>Période des temps (en images d'analyse) : autocorrélation pondérée autour de 120 BPM.</summary>
    private static double EstimatePeriod(double[] onset)
    {
        var minLag = (int)(FrameRate * 60 / 200);
        var maxLag = (int)(FrameRate * 60 / 60);
        double bestScore = double.MinValue, bestLag = FrameRate / 2;
        for (var lag = minLag; lag <= maxLag && lag < onset.Length / 2; lag++)
        {
            double ac = 0;
            for (var i = lag; i < onset.Length; i++) ac += onset[i] * onset[i - lag];
            ac /= onset.Length - lag;
            var bpm = 60 * FrameRate / lag;
            var weight = Math.Exp(-0.5 * Math.Pow(Math.Log2(bpm / 120.0) / 0.9, 2));
            var score = ac * weight;
            if (score > bestScore) (bestScore, bestLag) = (score, lag);
        }
        // Affinage à la sous-image près (parabole sur l'autocorrélation).
        return bestLag;
    }

    /// <summary>Suivi des temps par programmation dynamique (Ellis 2007).</summary>
    private static List<int> TrackBeats(double[] onset, double period)
    {
        var n = onset.Length;
        // Score local : enveloppe lissée par une gaussienne de largeur période/32.
        var sigma = Math.Max(1, period / 32);
        var radius = (int)(3 * sigma);
        var local = new double[n];
        for (var i = 0; i < n; i++)
        {
            double s = 0;
            for (var k = -radius; k <= radius; k++)
            {
                var j = i + k;
                if (j >= 0 && j < n) s += onset[j] * Math.Exp(-0.5 * k * k / (sigma * sigma));
            }
            local[i] = s;
        }

        const double tightness = 100;
        var score = new double[n];
        var back = new int[n];
        for (var i = 0; i < n; i++)
        {
            var best = double.MinValue;
            var bestPrev = -1;
            var from = (int)Math.Round(i - 2 * period);
            var to = (int)Math.Round(i - period / 2);
            for (var p = Math.Max(0, from); p <= to; p++)
            {
                var pen = -tightness * Math.Pow(Math.Log((i - p) / period), 2);
                var v = score[p] + pen;
                if (v > best) (best, bestPrev) = (v, p);
            }
            score[i] = local[i] + (bestPrev >= 0 ? best : 0);
            back[i] = bestPrev;
        }

        // Dernier temps : le meilleur score parmi les maxima locaux de la dernière période.
        var start = Math.Max(0, n - (int)Math.Ceiling(period));
        var last = start;
        for (var i = start; i < n; i++) if (score[i] > score[last]) last = i;

        var beats = new List<int>();
        for (var i = last; i >= 0; i = back[i])
        {
            beats.Add(i);
            if (back[i] < 0) break;
        }
        beats.Reverse();

        // Supprime d'éventuels temps parasites dans le silence du début.
        var threshold = 0.1 * local.Max();
        while (beats.Count > 2 && local[beats[0]] < threshold) beats.RemoveAt(0);
        return beats;
    }

    private static double[] EnergyEnvelope(float[] x, double step)
    {
        var size = (int)(step * SampleRate);
        var count = x.Length / size + 1;
        var energy = new double[count];
        for (var i = 0; i < count; i++)
        {
            double s = 0;
            var end = Math.Min(x.Length, (i + 1) * size);
            for (var j = i * size; j < end; j++) s += x[j] * x[j];
            energy[i] = Math.Sqrt(s / Math.Max(1, end - i * size));
        }
        var max = energy.DefaultIfEmpty(0).Max();
        if (max > 0) for (var i = 0; i < count; i++) energy[i] /= max;
        return energy;
    }

    private static void Fft(double[] re, double[] im)
    {
        var n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (var len = 2; len <= n; len <<= 1)
        {
            var ang = -2 * Math.PI / len;
            var wr = Math.Cos(ang);
            var wi = Math.Sin(ang);
            for (var i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (var k = 0; k < len / 2; k++)
                {
                    var ur = re[i + k];
                    var ui = im[i + k];
                    var vr = re[i + k + len / 2] * cr - im[i + k + len / 2] * ci;
                    var vi = re[i + k + len / 2] * ci + im[i + k + len / 2] * cr;
                    re[i + k] = ur + vr;
                    im[i + k] = ui + vi;
                    re[i + k + len / 2] = ur - vr;
                    im[i + k + len / 2] = ui - vi;
                    (cr, ci) = (cr * wr - ci * wi, cr * wi + ci * wr);
                }
            }
        }
    }

    private async Task<float[]> DecodeAsync(string path, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(options.Value.FfmpegPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-i", path, "-vn", "-ac", "1",
                     "-ar", SampleRate.ToString(CultureInfo.InvariantCulture), "-f", "f32le", "pipe:1" })
            psi.ArgumentList.Add(a);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("FFmpeg n'a pas démarré");
        var errors = process.StandardError.ReadToEndAsync(ct);
        using var memory = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(memory, ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0) throw new InvalidDataException($"Son illisible : {(await errors).Trim()}");
        var bytes = memory.ToArray();
        var samples = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 4);
        return samples;
    }
}
