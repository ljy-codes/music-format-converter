namespace MusicFormatConverter.Core.Tests;

public class AudioPolicyTests
{
    private readonly SourceFile source = new("song.flac", "album/song.flac");
    private static AudioInfo Info(string format = "flac", string codec = "flac", int bits = 24,
        string sample = "s32", int rate = 96000, int channels = 2) =>
        new(format, codec, rate, channels, bits, sample, 10, 1000, 0, null,
            new Dictionary<string, string>());

    [Theory]
    [InlineData("mp3", "mp3", "fltp", 0, PlanAction.Copy, ".mp3")]
    [InlineData("mov,mp4,m4a,3gp,3g2,mj2", "aac", "fltp", 0, PlanAction.Copy, ".m4a")]
    [InlineData("mov,mp4,m4a,3gp,3g2,mj2", "alac", "s32p", 24, PlanAction.Copy, ".m4a")]
    [InlineData("aac", "aac", "fltp", 0, PlanAction.Remux, ".m4a")]
    [InlineData("flac", "flac", "s32", 24, PlanAction.EncodeAlac, ".m4a")]
    [InlineData("ogg", "opus", "fltp", 0, PlanAction.EncodeAlac, ".m4a")]
    [InlineData("wav", "pcm_s24le", "s32", 24, PlanAction.Copy, ".wav")]
    public void Smart_policy_uses_content_not_suffix(string format, string codec, string sample, int bits,
        PlanAction expected, string extension)
    {
        var plan = AudioPolicy.Plan(source, Info(format, codec, bits, sample, 48000), ConversionMode.Smart);
        Assert.Equal(expected, plan.Action);
        Assert.Equal(extension, plan.OutputExtension);
    }

    [Fact]
    public void Explicit_alac_mode_overrides_compatible_copy() =>
        Assert.Equal(PlanAction.EncodeAlac, AudioPolicy.Plan(source,
            Info("mp3", "mp3", 0, "fltp", 44100), ConversionMode.Alac).Action);

    [Fact]
    public void Explicit_aac_mode_does_not_reencode_existing_aac() =>
        Assert.Equal(PlanAction.Copy, AudioPolicy.Plan(source,
            Info("mov,mp4,m4a", "aac", 0, "fltp", 44100), ConversionMode.Aac).Action);

    [Fact]
    public void Aac_mode_encodes_flac() =>
        Assert.Equal(PlanAction.EncodeAac, AudioPolicy.Plan(source, Info(rate: 48000), ConversionMode.Aac).Action);

    [Theory]
    [InlineData("pcm_f32le", "flt", 32, 48000, 2)]
    [InlineData("flac", "s32", 32, 48000, 2)]
    [InlineData("flac", "s32", 24, 48000, 6)]
    [InlineData("dsd_lsbf_planar", "fltp", 0, 352800, 2)]
    [InlineData("flac", "s32", 24, 384000, 2)]
    [InlineData("flac", "s32", 0, 48000, 2)]
    public void Does_not_silently_reduce_unknown_or_special_precision(string codec, string sample, int bits, int rate, int channels) =>
        Assert.Equal(PlanAction.Unsupported,
            AudioPolicy.Plan(source, Info("wav", codec, bits, sample, rate, channels), ConversionMode.Smart).Action);

    [Fact]
    public void Output_estimate_is_not_a_fixed_lossy_multiplier()
    {
        var plan = AudioPolicy.Plan(source, Info(), ConversionMode.Smart);
        Assert.True(plan.EstimatedBytes >= 10 * 96000 * 2 * 3);
    }
}
