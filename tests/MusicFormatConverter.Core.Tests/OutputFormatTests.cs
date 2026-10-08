namespace MusicFormatConverter.Core.Tests;

public class OutputFormatTests
{
    [EngineFact]
    public async Task Existing_mp3_preview_does_not_require_mp3_encoder()
    {
        using var folder = new TestFolder();
        var mp3 = ConversionTests.Fixture(folder, "sine-997hz.mp3", "copy.mp3");
        var engines = EnginePaths.Discover() with { Ffmpeg = Path.Combine(folder.Path, "not-installed-encoder") };
        var options = new ConversionOptions(Path.Combine(folder.Path, "out"), ConversionMode.Mp3);
        var preview = await new MusicConverter(engines).PreviewAsync([mp3], options);
        Assert.Equal(PlanAction.Copy, Assert.Single(preview.Files).Action);
    }

    [Theory]
    [InlineData(3, "mp3", "mp3", 0, "fltp")]
    [InlineData(4, "flac", "flac", 24, "s32")]
    [InlineData(5, "wav", "pcm_s24le", 24, "s32")]
    public void Matching_format_is_copied_without_reencoding(int mode, string format, string codec, int bits, string sample)
    {
        var info = new AudioInfo(format, codec, 44100, 2, bits, sample, 5, 12345,
            0, null, new Dictionary<string, string>());
        Assert.Equal(PlanAction.Copy, AudioPolicy.Plan(new("song", "song"), info, (ConversionMode)mode).Action);
    }

    [Theory]
    [InlineData(3, ".mp3", "EncodeMp3")]
    [InlineData(4, ".flac", "EncodeFlac")]
    [InlineData(5, ".wav", "EncodeWav")]
    public void Explicit_output_format_is_not_forced_to_apple_alac(int mode, string extension, string action)
    {
        var info = new AudioInfo("asf", "wmav2", 44100, 2, 0, "fltp", 5, 12345,
            0, null, new Dictionary<string, string>());
        var plan = AudioPolicy.Plan(new("source.wma", "source.wma"), info, (ConversionMode)mode);
        Assert.Equal(extension, plan.OutputExtension);
        Assert.Equal(action, plan.Action.ToString());
    }

    [Fact]
    public void Mp3_does_not_silently_resample_high_resolution_audio()
    {
        var info = new AudioInfo("flac", "flac", 96000, 2, 24, "s32", 5, 12345,
            0, null, new Dictionary<string, string>());
        var plan = AudioPolicy.Plan(new("source.flac", "source.flac"), info, (ConversionMode)3);
        Assert.Equal(PlanAction.Unsupported, plan.Action);
        Assert.Contains("采样率", plan.Reason);
    }

    [EngineFact]
    public async Task Flac_and_wav_roundtrip_keep_all_24bit_samples_and_isolate_bad_input()
    {
        using var folder = new TestFolder();
        var raw = new byte[48000 * 2 * 3];
        new Random(510).NextBytes(raw);
        var pcm = Path.Combine(folder.Path, "source.pcm");
        await File.WriteAllBytesAsync(pcm, raw);
        var wav = Path.Combine(folder.Path, "source.wav");
        await ConversionTests.Engine("-v", "error", "-f", "s24le", "-ar", "48000", "-ac", "2", "-i", pcm,
            "-metadata", "title=完整精度", "-c:a", "pcm_s24le", wav);
        var bad = folder.File("bad.flac", "invalid audio");
        var converter = new MusicConverter();
        var source = wav;
        foreach (var mode in new[] { ConversionMode.Flac, ConversionMode.Wav })
        {
            var options = new ConversionOptions(Path.Combine(folder.Path, mode.ToString()), mode);
            var plans = await converter.PreviewAsync([source, bad], options);
            var results = await converter.ConvertAsync(plans.Files, options);
            Assert.True(results[0].Status == ItemStatus.Succeeded, results[0].Message);
            Assert.Equal(ItemStatus.Skipped, results[1].Status);
            var info = await new AudioProbe(EnginePaths.Discover()).ReadAsync(results[0].OutputPath!);
            Assert.Equal(mode == ConversionMode.Flac ? "flac" : "pcm_s24le", info.CodecName);
            Assert.Equal(24, info.BitsPerSample);
            Assert.Equal(48000, info.SampleRate);
            Assert.Equal("完整精度", info.Tags["title"]);
            var decoded = Path.Combine(folder.Path, mode + ".pcm");
            await ConversionTests.Engine("-v", "error", "-i", results[0].OutputPath!, "-c:a", "pcm_s24le",
                "-f", "s24le", decoded);
            Assert.Equal(raw, await File.ReadAllBytesAsync(decoded));
            source = results[0].OutputPath!;
            Assert.Empty(Directory.GetFiles(options.OutputDirectory, ".mfc-*", SearchOption.AllDirectories));
        }
        Assert.Equal(raw, await File.ReadAllBytesAsync(pcm));
    }

    [EngineFact]
    public async Task Mp3_encodes_at_requested_rate_and_existing_mp3_is_copied()
    {
        using var folder = new TestFolder();
        var wav = await ConversionTests.Generate(folder, "source.wav", "-c:a", "pcm_s24le");
        var converter = new MusicConverter();
        var options = new ConversionOptions(Path.Combine(folder.Path, "out"), ConversionMode.Mp3);
        var preview = await converter.PreviewAsync([wav], options);
        var encoders = await ProcessRunner.RunAsync(EnginePaths.Discover().Ffmpeg, ["-hide_banner", "-encoders"], TimeSpan.FromSeconds(15));
        if (!encoders.StandardOutput.Contains("libmp3lame"))
        {
            Assert.Equal(PlanAction.Unsupported, preview.Files[0].Action);
            Assert.Contains("编码器", preview.Files[0].Reason);
            return; // Missing optional codec is an explicit tested result, not an unreported skip.
        }
        Assert.Equal(PlanAction.EncodeMp3, preview.Files[0].Action);
        var result = Assert.Single(await converter.ConvertAsync(preview.Files, options));
        Assert.True(result.Status == ItemStatus.Succeeded, result.Message);
        var info = await new AudioProbe(EnginePaths.Discover()).ReadAsync(result.OutputPath!);
        Assert.Equal("mp3", info.CodecName);
        Assert.Equal(48000, info.SampleRate);
        Assert.Equal("中文标题", info.Tags["title"]);
        var bitrate = await ProcessRunner.RunAsync(EnginePaths.Discover().Ffprobe,
            ["-v", "error", "-select_streams", "a:0", "-show_entries", "stream=bit_rate", "-of", "csv=p=0", result.OutputPath!],
            TimeSpan.FromSeconds(15));
        Assert.Equal("320000", bitrate.StandardOutput.Trim());
        var next = options with { OutputDirectory = Path.Combine(folder.Path, "next") };
        var copyPlan = await converter.PreviewAsync([result.OutputPath!], next);
        Assert.Equal(PlanAction.Copy, copyPlan.Files[0].Action);
        var copy = Assert.Single(await converter.ConvertAsync(copyPlan.Files, next));
        Assert.True(copy.Status == ItemStatus.Succeeded, copy.Message);
        Assert.Equal(await File.ReadAllBytesAsync(result.OutputPath!), await File.ReadAllBytesAsync(copy.OutputPath!));
    }

    [EngineFact]
    public async Task New_formats_handle_cover_and_metadata_without_truncating_audio()
    {
        using var folder = new TestFolder();
        var wav = await ConversionTests.Generate(folder, "source.wav", "-c:a", "pcm_s16le");
        var png = Path.Combine(folder.Path, "cover.png");
        await ConversionTests.Engine("-v", "error", "-f", "lavfi", "-i", "color=blue:size=64x64", "-frames:v", "1", png);
        var song = Path.Combine(folder.Path, "cover.m4a");
        await ConversionTests.Engine("-v", "error", "-i", wav, "-i", png, "-map", "0:a", "-map", "1:v",
            "-c:a", "alac", "-c:v", "copy", "-disposition:v", "attached_pic", song);
        var converter = new MusicConverter();
        foreach (var mode in new[] { ConversionMode.Flac, ConversionMode.Wav, ConversionMode.Mp3 })
        {
            var options = new ConversionOptions(Path.Combine(folder.Path, mode.ToString()), mode);
            var plans = await converter.PreviewAsync([song], options);
            if (plans.Files[0].Action == PlanAction.Unsupported && mode == ConversionMode.Mp3)
            { Assert.Contains("编码器", plans.Files[0].Reason); continue; }
            var result = Assert.Single(await converter.ConvertAsync(plans.Files, options));
            Assert.True(result.Status == ItemStatus.Succeeded, result.Message);
            var info = await new AudioProbe(EnginePaths.Discover()).ReadAsync(result.OutputPath!);
            Assert.Equal(mode == ConversionMode.Wav ? 0 : 1, info.CoverCount);
            if (mode == ConversionMode.Wav) Assert.Contains("封面未保留", result.Message);
            Assert.Equal("中文标题", info.Tags["title"]);

            var strippedOptions = options with { PreserveMetadata = false, OutputDirectory = options.OutputDirectory + "-stripped" };
            var strippedPlans = await converter.PreviewAsync([song], strippedOptions);
            var stripped = Assert.Single(await converter.ConvertAsync(strippedPlans.Files, strippedOptions));
            Assert.True(stripped.Status == ItemStatus.Succeeded, stripped.Message);
            var strippedInfo = await new AudioProbe(EnginePaths.Discover()).ReadAsync(stripped.OutputPath!);
            Assert.Equal(0, strippedInfo.CoverCount);
            Assert.False(strippedInfo.Tags.ContainsKey("title"));
        }
    }

    [EngineFact]
    public async Task Wma_to_new_lossless_formats_has_exact_decoded_24bit_pcm()
    {
        using var folder = new TestFolder();
        var wma = await ConversionTests.Generate(folder, "source.wma", "-c:a", "wmav2");
        var original = Path.Combine(folder.Path, "source.pcm");
        await ConversionTests.Engine("-v", "error", "-i", wma, "-c:a", "pcm_s24le", "-f", "s24le", original);
        foreach (var mode in new[] { ConversionMode.Flac, ConversionMode.Wav })
        {
            var options = new ConversionOptions(Path.Combine(folder.Path, mode.ToString()), mode, VerifyAudio: false);
            var converter = new MusicConverter();
            var plans = await converter.PreviewAsync([wma], options);
            var result = Assert.Single(await converter.ConvertAsync(plans.Files, options));
            Assert.True(result.Status == ItemStatus.Succeeded, result.Message);
            var decoded = Path.Combine(folder.Path, mode + ".pcm");
            await ConversionTests.Engine("-v", "error", "-i", result.OutputPath!, "-c:a", "pcm_s24le", "-f", "s24le", decoded);
            Assert.Equal(await File.ReadAllBytesAsync(original), await File.ReadAllBytesAsync(decoded));
        }
    }
}
