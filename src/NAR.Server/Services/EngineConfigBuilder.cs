using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NAR.Core.Config;
using NAR.Server.Data;

namespace NAR.Server.Services;

/// <summary>
/// Traduit une chaîne de la base en configuration moteur (même format que channel.json).
/// La révision est une empreinte du contenu : elle change dès que quoi que ce soit change
/// (conducteur, gain d'un média réanalysé…), et le moteur recharge alors à chaud.
/// </summary>
public sealed class EngineConfigBuilder(IDbContextFactory<NarDbContext> dbFactory, IOptions<NarOptions> options, Network.SmbSources smb)
{
    public async Task<ChannelConfig?> BuildAsync(int channelId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var channel = await db.Channels.AsNoTracking()
            .Include(c => c.FillerAsset)
            .Include(c => c.Entries.OrderBy(e => e.Position)).ThenInclude(e => e.Asset)
            .FirstOrDefaultAsync(c => c.Id == channelId, ct);
        return channel is null ? null : Build(channel);
    }

    public ChannelConfig Build(Channel channel)
    {
        var o = options.Value;
        var dir = o.ChannelDirectory(channel.Id);
        var config = new ChannelConfig
        {
            Name = channel.Name,
            Format = new FormatConfig { Width = channel.Width, Height = channel.Height, FrameRate = channel.FrameRate },
            Encoder = new EncoderConfig
            {
                Video = channel.VideoEncoder,
                Preset = string.IsNullOrWhiteSpace(channel.EncoderPreset) ? null : channel.EncoderPreset,
                VideoBitrateKbps = channel.VideoBitrateKbps,
                AudioBitrateKbps = channel.AudioBitrateKbps,
            },
            Hls = new HlsConfig { Directory = Path.Combine(dir, "hls"), SegmentSeconds = channel.SegmentSeconds, ListSize = 15 },
            StatusPort = EnginePort(channel),
            StatusBind = "127.0.0.1",
            FfmpegPath = o.FfmpegPath,
            FfprobePath = o.FfprobePath,
            PreloadSeconds = channel.PreloadSeconds,
            Filler = channel.FillerAsset is { Status: AssetStatus.Pret } filler ? smb.Playable(filler) : null,
            AsRunDirectory = Path.Combine(dir, "asrun"),
            Overlays = Overlays(channel, dir),
            Playlist = new PlaylistConfig
            {
                Loop = channel.Loop,
                Items = channel.Entries
                    .OrderBy(e => e.Position)
                    .Where(e => e.Asset is not null)
                    .Select(e => new ItemConfig
                    {
                        Id = e.Key,
                        Title = e.DisplayTitle,
                        Path = smb.Playable(e.Asset!),
                        In = e.InPoint is { } i && i > TimeSpan.Zero ? i.ToString("c", CultureInfo.InvariantCulture) : null,
                        Out = e.OutPoint is { } x && x > TimeSpan.Zero ? x.ToString("c", CultureInfo.InvariantCulture) : null,
                        StartAt = e.StartMode == StartMode.HeureFixe && e.FixedStart is { } at ? at.ToString("O", CultureInfo.InvariantCulture) : null,
                        GainDb = e.Asset.GainDb,
                        Overlay = e.Asset.Kind is not (AssetKind.Spot or AssetKind.Jingle or AssetKind.BandeAnnonce or AssetKind.Habillage),
                        Programme = e.Asset.Kind is AssetKind.Programme or AssetKind.Episode or AssetKind.Film,
                        Rating = Signaletique.Of(e.Asset),
                    })
                    .ToList(),
            },
        };
        config.Revision = Fingerprint(config);
        return config;
    }

    private static OverlayConfig Overlays(Channel channel, string dir)
    {
        var logo = Path.Combine(dir, "logo.png");
        var hasLogo = channel.OverlayLogo && channel.HasLogo && File.Exists(logo);
        return new OverlayConfig
        {
            Logo = hasLogo ? logo : null,
            LogoVersion = hasLogo ? File.GetLastWriteTimeUtc(logo).Ticks : 0,
            Corner = channel.OverlayCorner,
            LogoHeightPercent = channel.OverlayLogoSize,
            LogoOpacity = channel.OverlayLogoOpacity,
            Clock = channel.OverlayClock,
            UpNext = channel.OverlayUpNext,
            UpNextBeforeEndSeconds = Math.Max(15, channel.OverlayUpNextBefore),
            UpNextDurationSeconds = 10,
            Rating = channel.OverlayRating,
            RatingAlways = channel.OverlayRatingAlways,
        };
    }

    public int EnginePort(Channel channel) => channel.EnginePort > 0 ? channel.EnginePort : options.Value.EngineBasePort + channel.Id;

    private static long Fingerprint(ChannelConfig config)
    {
        var json = JsonSerializer.Serialize(config, ChannelConfig.JsonOptions);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return BitConverter.ToInt64(hash, 0) & long.MaxValue;
    }
}
