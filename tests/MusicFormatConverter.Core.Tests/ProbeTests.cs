namespace MusicFormatConverter.Core.Tests;

public class ProbeTests
{
    [Theory]
    [InlineData("s16p", 0, 16)]
    [InlineData("s16", 0, 16)]
    [InlineData("s32p", 24, 24)]
    [InlineData("s16p", 24, 24)]
    [InlineData("s32p", 0, 0)]
    [InlineData("fltp", 0, 0)]
    [InlineData("", 0, 0)]
    public void Wma_lossless_precision_uses_only_trustworthy_16bit_fallback(string sample, int declared, int expected)
    {
        var info = AudioProbe.Parse($$$"""
            {"streams":[{"index":0,"codec_type":"audio","codec_name":"wmalossless",
            "sample_rate":"44100","channels":2,"sample_fmt":"{{{sample}}}","bits_per_raw_sample":"{{{declared}}}",
            "bits_per_sample":0}],"format":{"format_name":"asf"}}
            """, 100);
        Assert.Equal(expected, info.BitsPerSample);
        var plan = AudioPolicy.Plan(new("sample.wma", "sample.wma"), info, ConversionMode.Smart);
        Assert.Equal(expected == 0 ? PlanAction.Unsupported : PlanAction.EncodeAlac, plan.Action);
    }

    [Fact]
    public void Unknown_precision_in_other_lossless_codecs_is_not_guessed()
    {
        var info = AudioProbe.Parse("""
            {"streams":[{"codec_type":"audio","codec_name":"flac","sample_rate":"44100",
            "channels":2,"sample_fmt":"s16"}],"format":{"format_name":"flac"}}
            """, 100);
        Assert.Equal(0, info.BitsPerSample);
    }

    [Theory]
    [InlineData("", 16, PlanAction.EncodeAlac)]
    [InlineData(",\"bits_per_sample\":24", 24, PlanAction.EncodeAlac)]
    [InlineData(",\"bits_per_sample\":32", 32, PlanAction.Unsupported)]
    [InlineData(",\"bits_per_raw_sample\":\"32\",\"bits_per_sample\":16", 32, PlanAction.Unsupported)]
    public void Wma_declared_precision_takes_priority_over_decoder_storage(string fields, int bits, PlanAction action)
    {
        var info = AudioProbe.Parse($$$"""
            {"streams":[{"codec_type":"audio","codec_name":"wmalossless","sample_rate":"44100",
            "channels":2,"sample_fmt":"s16p"{{{fields}}}}],"format":{"format_name":"asf"}}
            """, 100);
        Assert.Equal(bits, info.BitsPerSample);
        Assert.Equal(action, AudioPolicy.Plan(new("sample.wma", "sample.wma"), info, ConversionMode.Smart).Action);
    }

    [Fact]
    public void Missing_audio_parameters_report_unreadable_instead_of_unsupported_channel_layout()
    {
        var ex = Assert.Throws<AudioProcessingException>(() => AudioProbe.Parse(
            """{"streams":[{"codec_type":"audio","codec_name":"flac","sample_rate":"0","channels":0}],"format":{"format_name":"flac"}}""", 20));
        Assert.Contains("损坏", ex.Message);
    }

    [Fact]
    public void Parses_precision_and_merges_container_and_stream_tags()
    {
        const string json = """
          {"streams":[{"index":0,"codec_type":"audio","codec_name":"flac","sample_rate":"96000",
          "channels":2,"sample_fmt":"s32","bits_per_raw_sample":"24","duration":"1.2",
          "tags":{"TITLE":"中文"}},{"index":1,"codec_type":"video","codec_name":"mjpeg","disposition":{"attached_pic":1}}],
          "format":{"format_name":"flac","duration":"1.2","tags":{"album":"专辑"}}}
          """;
        var info = AudioProbe.Parse(json, 300);
        Assert.Equal(24, info.BitsPerSample);
        Assert.Equal(1, info.CoverStreamIndex);
        Assert.Equal("中文", info.Tags["title"]);
        Assert.Equal("专辑", info.Tags["album"]);
    }

    [Fact]
    public void Multiple_audio_streams_are_not_silently_discarded()
    {
        Assert.Throws<AudioProcessingException>(() => AudioProbe.Parse(
            """{"streams":[{"codec_type":"audio"},{"codec_type":"audio"}],"format":{}}""", 10));
    }

    [Fact]
    public void Video_not_cover_is_rejected()
    {
        Assert.Throws<AudioProcessingException>(() => AudioProbe.Parse(
            """{"streams":[{"codec_type":"audio"},{"codec_type":"video","disposition":{"attached_pic":0}}],"format":{}}""", 10));
    }

    [Fact]
    public void Encrypted_stream_gets_specific_reason()
    {
        var ex = Assert.Throws<AudioProcessingException>(() => AudioProbe.Parse(
            """{"streams":[{"codec_type":"audio","codec_tag_string":"enca"}],"format":{}}""", 10));
        Assert.Contains("保护", ex.Message);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{")]
    [InlineData("""{"streams":[],"format":{}}""")]
    public void Malformed_probe_cannot_be_success(string json) =>
        Assert.Throws<AudioProcessingException>(() => AudioProbe.Parse(json, 10));
}
