using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using NAR.Core.Media;

namespace NAR.Engine.Playout;

internal static class FramePool
{
    public static readonly ArrayPool<byte> Shared = ArrayPool<byte>.Create(maxArrayLength: 64 * 1024 * 1024, maxArraysPerBucket: 64);
}

/// <summary>
/// Une source fichier : un FFmpeg qui décode et normalise le média au format de la chaîne
/// (vidéo brute sur stdout, audio PCM sur une socket locale). Le moteur tire une image à la fois ;
/// la file bornée sert de préchargement et de contre-pression.
/// </summary>
public sealed class SourceDecoder : IAsyncDisposable
{
    private readonly string _path;
    private readonly long _startFrames;     // In + décalage, en images
    private readonly long _maxFrames;
    private readonly bool _loop;
    private readonly double _gainDb;
    private readonly ProbeResult _probe;
    private readonly ChannelFormat _format;
    private readonly string _ffmpeg;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<byte[]> _video;
    private readonly AudioBuffer _audio;

    private Process? _process;
    private TcpListener? _listener;
    private volatile string? _failure;
    private string? _lastError;
    private long _delivered;
    private int _audioDebt;

    public SourceDecoder(string label, string path, long startFrames, long maxFrames, bool loop, double gainDb,
        ProbeResult probe, ChannelFormat format, string ffmpegPath, ILogger logger, int bufferFrames)
    {
        Label = label;
        _path = path;
        _startFrames = startFrames;
        _maxFrames = maxFrames;
        _loop = loop;
        _gainDb = gainDb;
        _probe = probe;
        _format = format;
        _ffmpeg = ffmpegPath;
        _logger = logger;
        _video = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(Math.Max(2, bufferFrames))
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        var seconds = Math.Max(2, (int)Math.Ceiling((double)bufferFrames / format.Rate.FramesPerSecond) + 2);
        _audio = new AudioBuffer(format.SampleRate * format.AudioChannels * ChannelFormat.BytesPerSample * seconds);
    }

    public string Label { get; }
    public long Delivered => Interlocked.Read(ref _delivered);
    public int Buffered => _video.Reader.Count;
    public string? Failure => _failure;

    /// <summary>Vrai quand la source n'a plus rien à donner (fin, point de sortie atteint, ou échec).</summary>
    public bool IsFinished => _failure is not null || _video.Reader.Completion.IsCompleted;

    public void Fail(string reason)
    {
        _failure ??= reason;
        _logger.LogWarning("Source {Label} en échec : {Reason}", Label, reason);
    }

    public void Start()
    {
        if (!_probe.Ok)
        {
            Fail(_probe.Error ?? "Média illisible");
            _video.Writer.TryComplete();
            return;
        }
        if (_maxFrames <= 0)
        {
            _video.Writer.TryComplete();
            return;
        }

        int port = 0;
        if (_probe.HasAudio)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start(1);
            port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        }
        else
        {
            _audio.Complete();
        }

        try
        {
            _process = FfmpegProcess.Start(_ffmpeg, BuildArgs(port), redirectInput: false, redirectOutput: true,
                _logger, Label, line => _lastError = line);
        }
        catch (Exception ex)
        {
            Fail($"FFmpeg introuvable ou impossible à lancer : {ex.Message}");
            _video.Writer.TryComplete();
            _listener?.Stop();
            return;
        }

        _ = Task.Run(ReadVideoAsync);
        if (_probe.HasAudio)
            _ = Task.Factory.StartNew(ReadAudio, TaskCreationOptions.LongRunning);
    }

    private IEnumerable<string> BuildArgs(int audioPort)
    {
        var f = _format;
        var ci = CultureInfo.InvariantCulture;
        var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error" };
        if (_loop) args.AddRange(["-stream_loop", "-1"]);

        var seek = f.Rate.TimeFromFrames(_startFrames);
        if (seek > TimeSpan.Zero) args.AddRange(["-ss", seek.TotalSeconds.ToString("0.000", ci)]);
        args.AddRange(["-i", _path]);

        // Vidéo : désentrelacement si besoin, mise à l'échelle avec bandes noires, cadence de la chaîne.
        var vf = string.Create(ci,
            $"bwdif=deint=interlaced,scale={f.Width}:{f.Height}:force_original_aspect_ratio=decrease:flags=bicubic," +
            $"pad={f.Width}:{f.Height}:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1,fps={f.Rate.Numerator}/{f.Rate.Denominator},format=yuv420p");
        args.AddRange(["-map", "0:v:0", "-vf", vf, "-f", "rawvideo", "pipe:1"]);

        if (audioPort > 0)
        {
            var af = string.Create(ci,
                $"aresample={f.SampleRate}:async=1,volume={_gainDb:0.0#}dB,aformat=sample_fmts=s16:channel_layouts=stereo");
            args.AddRange(["-map", "0:a:0", "-af", af, "-ac", f.AudioChannels.ToString(ci), "-ar", f.SampleRate.ToString(ci),
                "-f", "s16le", $"tcp://127.0.0.1:{audioPort}"]);
        }
        return args;
    }

    private async Task ReadVideoAsync()
    {
        var frameBytes = _format.VideoFrameBytes;
        long read = 0;
        try
        {
            var stream = _process!.StandardOutput.BaseStream;
            while (read < _maxFrames && !_cts.IsCancellationRequested)
            {
                var buffer = FramePool.Shared.Rent(frameBytes);
                var got = await stream.ReadAtLeastAsync(buffer.AsMemory(0, frameBytes), frameBytes,
                    throwOnEndOfStream: false, _cts.Token);
                if (got < frameBytes)
                {
                    FramePool.Shared.Return(buffer);
                    break;
                }
                await _video.Writer.WriteAsync(buffer, _cts.Token);
                read++;
            }

            if (read == 0 && !_cts.IsCancellationRequested)
            {
                await _process.WaitForExitAsync(_cts.Token).WaitAsync(TimeSpan.FromSeconds(2));
                if (_process.HasExited && _process.ExitCode != 0)
                    Fail(_lastError ?? $"FFmpeg a quitté avec le code {_process.ExitCode}");
            }
        }
        catch (OperationCanceledException) { }
        catch (TimeoutException) { }
        catch (Exception ex)
        {
            if (!_cts.IsCancellationRequested) Fail($"Lecture vidéo interrompue : {ex.Message}");
        }
        finally
        {
            _video.Writer.TryComplete();
            // Point de sortie atteint : on arrête le décodeur, inutile qu'il continue.
            if (read >= _maxFrames) FfmpegProcess.Kill(_process);
        }
    }

    private void ReadAudio()
    {
        try
        {
            using var client = _listener!.AcceptTcpClientAsync(_cts.Token).AsTask().GetAwaiter().GetResult();
            using var stream = client.GetStream();
            var chunk = new byte[16 * 1024];
            int n;
            while (!_cts.IsCancellationRequested && (n = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (!_audio.Write(chunk.AsSpan(0, n))) break;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
            // fin de flux ou arrêt demandé
        }
        finally
        {
            _audio.Complete();
            try { _listener?.Stop(); } catch { }
        }
    }

    /// <summary>
    /// Donne l'image suivante (vidéo + audio de la même image). Faux si rien n'est prêt.
    /// L'audio manquant est remplacé par du silence et rattrapé ensuite pour rester synchro.
    /// </summary>
    public bool TryReadFrame(Span<byte> video, Span<byte> audio)
    {
        if (_failure is not null || !_video.Reader.TryRead(out var buffer)) return false;

        buffer.AsSpan(0, video.Length).CopyTo(video);
        FramePool.Shared.Return(buffer);
        Interlocked.Increment(ref _delivered);

        if (!_probe.HasAudio)
        {
            audio.Clear();
            return true;
        }

        if (_audioDebt > 0) _audioDebt -= _audio.Skip(_audioDebt);
        var got = _audio.Read(audio);
        if (got < audio.Length)
        {
            audio[got..].Clear();
            if (!_audio.IsCompleted) _audioDebt += audio.Length - got;
        }
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _audio.Cancel();
        FfmpegProcess.Kill(_process);
        try { _listener?.Stop(); } catch { }
        _video.Writer.TryComplete();
        while (_video.Reader.TryRead(out var b)) FramePool.Shared.Return(b);
        if (_process is not null)
        {
            try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
            _process.Dispose();
        }
        _cts.Dispose();
    }
}
