namespace NAR.Core.Media;

/// <summary>
/// Format d'antenne d'une chaîne. Tout ce qui entre dans le moteur est ramené à ce format
/// (vidéo YUV 4:2:0, audio PCM 16 bits stéréo) avant d'être mixé et encodé.
/// </summary>
public sealed record ChannelFormat
{
    public ChannelFormat(int width, int height, FrameRate rate, int sampleRate = 48000, int audioChannels = 2)
    {
        if (width <= 0 || height <= 0 || width % 2 != 0 || height % 2 != 0)
            throw new ArgumentException("La résolution doit être positive et paire (YUV 4:2:0).");
        Width = width;
        Height = height;
        Rate = rate;
        SampleRate = sampleRate;
        AudioChannels = audioChannels;
    }

    public int Width { get; }
    public int Height { get; }
    public FrameRate Rate { get; }
    public int SampleRate { get; }
    public int AudioChannels { get; }

    public const int BytesPerSample = 2; // PCM s16le

    /// <summary>Taille d'une image YUV 4:2:0 planaire.</summary>
    public int VideoFrameBytes => Width * Height * 3 / 2;

    public int AudioBytesForFrame(long frame) =>
        Rate.SamplesForFrame(frame, SampleRate) * AudioChannels * BytesPerSample;

    /// <summary>Taille audio maximale d'une image (pour dimensionner les tampons).</summary>
    public int MaxAudioBytesPerFrame =>
        ((int)Math.Ceiling((double)SampleRate * Rate.Denominator / Rate.Numerator)) * AudioChannels * BytesPerSample;

    public override string ToString() => $"{Width}x{Height}@{Rate}";
}
