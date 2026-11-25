using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using NAR.Core.Config;
using NAR.Core.Media;

namespace NAR.Engine.Playout;

/// <summary>
/// L'encodeur unique de la chaîne : un FFmpeg qui ne s'arrête jamais et reçoit un flux continu
/// d'images brutes. Comme ses horodatages sont comptés à partir du nombre d'images, la sortie HLS
/// ne voit jamais de rupture, quel que soit le média qui change en amont.
/// </summary>
public sealed class OutputEncoder : IAsyncDisposable
{
    private readonly ChannelConfig _config;
    private readonly ChannelFormat _format;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<(byte[] Buffer, int Length)> _videoQueue;
    private readonly Channel<(byte[] Buffer, int Length)> _audioQueue;
    private Process? _process;
    private TcpListener? _listener;
    private volatile string? _failure;
    private string? _lastError;

    public OutputEncoder(ChannelConfig config, ChannelFormat format, ILogger logger)
    {
        _config = config;
        _format = format;
        _logger = logger;
        var capacity = Math.Max(4, format.Rate.NominalFps); // ~1 s d'avance maximum
        var options = new BoundedChannelOptions(capacity) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait };
        _videoQueue = Channel.CreateBounded<(byte[], int)>(options);
        _audioQueue = Channel.CreateBounded<(byte[], int)>(options);
    }

    public string? Failure => _failure;
    public bool HasFailed => _failure is not null || (_process?.HasExited ?? false);
    public string HlsDirectory => _config.Resolve(_config.Hls.Directory);
    public int QueuedFrames => _videoQueue.Reader.Count;

    public void Start()
    {
        Directory.CreateDirectory(HlsDirectory);
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start(1);
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _process = FfmpegProcess.Start(_config.FfmpegPath, BuildArgs(port), redirectInput: true, redirectOutput: false,
            _logger, "encodeur", line =>
            {
                if (line.Contains("Guessed Channel Layout", StringComparison.Ordinal)) return; // bruit normal
                _lastError = line;
                _logger.LogInformation("[encodeur] {Line}", line);
            });

        _ = Task.Run(WriteVideoAsync);
        _ = Task.Run(WriteAudioAsync);
        _logger.LogInformation("Encodeur démarré : {Format}, {Codec}, HLS dans {Dir}", _format, _config.Encoder.Video, HlsDirectory);
    }

    private IEnumerable<string> BuildArgs(int audioPort)
    {
        var f = _format;
        var e = _config.Encoder;
        var h = _config.Hls;
        var ci = CultureInfo.InvariantCulture;
        var gop = Math.Max(1, (int)Math.Round(f.Rate.FramesPerSecond * h.SegmentSeconds));

        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-loglevel", "warning",
            "-f", "rawvideo", "-pix_fmt", "yuv420p", "-s", $"{f.Width}x{f.Height}",
            "-framerate", $"{f.Rate.Numerator}/{f.Rate.Denominator}", "-thread_queue_size", "64", "-i", "pipe:0",
            "-f", "s16le", "-ar", f.SampleRate.ToString(ci), "-ac", f.AudioChannels.ToString(ci),
            "-thread_queue_size", "1024", "-i", $"tcp://127.0.0.1:{audioPort}",
            "-map", "0:v", "-map", "1:a",
            "-c:v", e.Video,
        };
        if (!string.IsNullOrWhiteSpace(e.Preset)) args.AddRange(["-preset", e.Preset!]);
        args.AddRange([
            "-b:v", $"{e.VideoBitrateKbps}k", "-maxrate", $"{e.VideoBitrateKbps}k", "-bufsize", $"{e.VideoBitrateKbps * 2}k",
            "-g", gop.ToString(ci), "-keyint_min", gop.ToString(ci),
            "-force_key_frames", $"expr:gte(t,n_forced*{h.SegmentSeconds})",
        ]);
        if (e.Video.StartsWith("libx26", StringComparison.Ordinal)) args.AddRange(["-sc_threshold", "0"]);
        args.AddRange(["-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", $"{e.AudioBitrateKbps}k", "-ar", f.SampleRate.ToString(ci)]);
        args.AddRange(e.ExtraArgs);

        var dir = HlsDirectory;
        args.AddRange([
            "-f", "hls",
            "-hls_time", h.SegmentSeconds.ToString(ci),
            "-hls_list_size", h.ListSize.ToString(ci),
            // Numéros de segment basés sur l'heure : un redémarrage de l'encodeur ne casse pas les lecteurs.
            "-hls_start_number_source", "epoch",
            "-hls_flags", "delete_segments+independent_segments+program_date_time+append_list+discont_start",
            "-hls_segment_filename", Path.Combine(dir, "seg_%010d.ts"),
            Path.Combine(dir, "index.m3u8"),
        ]);
        return args;
    }

    /// <summary>Envoie une image à l'encodeur. Attend s'il prend du retard (contre-pression = processeur saturé).</summary>
    public async ValueTask SendAsync(ReadOnlyMemory<byte> video, ReadOnlyMemory<byte> audio, CancellationToken ct)
    {
        var v = FramePool.Shared.Rent(video.Length);
        video.CopyTo(v);
        var a = FramePool.Shared.Rent(Math.Max(1, audio.Length));
        audio.CopyTo(a);
        await _videoQueue.Writer.WriteAsync((v, video.Length), ct);
        await _audioQueue.Writer.WriteAsync((a, audio.Length), ct);
    }

    private async Task WriteVideoAsync()
    {
        try
        {
            var stdin = _process!.StandardInput.BaseStream;
            await foreach (var (buffer, length) in _videoQueue.Reader.ReadAllAsync(_cts.Token))
            {
                try { await stdin.WriteAsync(buffer.AsMemory(0, length), _cts.Token); }
                finally { FramePool.Shared.Return(buffer); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _failure ??= $"Écriture vidéo vers l'encodeur impossible : {ExitInfo() ?? ex.Message}";
        }
    }

    private async Task WriteAudioAsync()
    {
        try
        {
            using var client = await _listener!.AcceptTcpClientAsync(_cts.Token);
            client.NoDelay = true;
            await using var stream = client.GetStream();
            await foreach (var (buffer, length) in _audioQueue.Reader.ReadAllAsync(_cts.Token))
            {
                try { await stream.WriteAsync(buffer.AsMemory(0, length), _cts.Token); }
                finally { FramePool.Shared.Return(buffer); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _failure ??= $"Écriture audio vers l'encodeur impossible : {ExitInfo() ?? ex.Message}";
        }
        finally
        {
            try { _listener?.Stop(); } catch { }
        }
    }

    private string? ExitInfo()
    {
        try
        {
            if (_process is { HasExited: true })
                return $"FFmpeg s'est arrêté (code {_process.ExitCode}){(_lastError is null ? "" : $" — {_lastError}")}";
        }
        catch (InvalidOperationException) { }
        return _lastError;
    }

    public async ValueTask DisposeAsync()
    {
        _videoQueue.Writer.TryComplete();
        _audioQueue.Writer.TryComplete();
        if (_process is not null && !_process.HasExited)
        {
            // Arrêt propre : on laisse l'encodeur finir son dernier segment.
            try
            {
                await Task.Delay(200);
                _process.StandardInput.Close();
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch { }
        }
        _cts.Cancel();
        FfmpegProcess.Kill(_process);
        try { _listener?.Stop(); } catch { }
        while (_videoQueue.Reader.TryRead(out var v)) FramePool.Shared.Return(v.Buffer);
        while (_audioQueue.Reader.TryRead(out var a)) FramePool.Shared.Return(a.Buffer);
        _process?.Dispose();
        _cts.Dispose();
    }
}
