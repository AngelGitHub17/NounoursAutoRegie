using NAR.Core.Media;

namespace NAR.Core.Tests;

public class FrameRateTests
{
    [Theory]
    [InlineData("25", 25, 1)]
    [InlineData("50", 50, 1)]
    [InlineData("30000/1001", 30000, 1001)]
    [InlineData("29.97", 30000, 1001)]
    [InlineData("59.94", 60000, 1001)]
    [InlineData("23.976", 24000, 1001)]
    public void Parse_accepte_les_formats_courants(string text, int num, int den)
    {
        var rate = FrameRate.Parse(text);
        Assert.Equal(num, rate.Numerator);
        Assert.Equal(den, rate.Denominator);
    }

    [Fact]
    public void Audio_25i_donne_1920_echantillons_par_image()
    {
        var rate = new FrameRate(25);
        for (var f = 0; f < 100; f++)
            Assert.Equal(1920, rate.SamplesForFrame(f, 48000));
    }

    [Fact]
    public void Audio_2997_reste_exact_sur_la_duree()
    {
        var rate = new FrameRate(30000, 1001);
        // 5 images en 29,97 = exactement 8008 échantillons à 48 kHz.
        var total = 0;
        for (var f = 0; f < 5; f++) total += rate.SamplesForFrame(f, 48000);
        Assert.Equal(8008, total);

        // Sur 24 h, aucune dérive : la somme colle à la durée réelle.
        long frames = rate.FramesFloor(TimeSpan.FromHours(24));
        long sum = 0;
        for (long f = 0; f < frames; f++) sum += rate.SamplesForFrame(f, 48000);
        var expected = (long)((Int128)frames * 48000 * 1001 / 30000);
        Assert.Equal(expected, sum);
    }

    [Fact]
    public void Conversions_images_temps_aller_retour()
    {
        var rate = new FrameRate(25);
        Assert.Equal(25, rate.FramesFloor(TimeSpan.FromSeconds(1)));
        Assert.Equal(90_000, rate.FramesFloor(TimeSpan.FromHours(1)));
        Assert.Equal(TimeSpan.FromSeconds(2), rate.TimeFromFrames(50));
        Assert.Equal(13, rate.FramesRound(TimeSpan.FromMilliseconds(510)));
        Assert.Equal(-25, rate.FramesRound(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void Timecode_affiche_heures_minutes_secondes_images()
    {
        var rate = new FrameRate(25);
        Assert.Equal("01:00:01:12", rate.ToTimecode(90_000 + 25 + 12));
    }

    [Fact]
    public void Format_calcule_les_tailles_de_tampon()
    {
        var format = new ChannelFormat(1920, 1080, new FrameRate(25));
        Assert.Equal(1920 * 1080 * 3 / 2, format.VideoFrameBytes);
        Assert.Equal(1920 * 2 * 2, format.AudioBytesForFrame(0));
        Assert.Throws<ArgumentException>(() => new ChannelFormat(1921, 1080, new FrameRate(25)));
    }
}
