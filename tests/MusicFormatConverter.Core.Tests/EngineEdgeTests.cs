namespace MusicFormatConverter.Core.Tests;

public class EngineEdgeTests
{
    [EngineFact]
    public Task Wma_smart_alac_preserves_decoded_samples_despite_asf_timebase() =>
        VerifyWmaAlac(ConversionMode.Smart, true, 0);

    [EngineFact]
    public Task Wma_explicit_alac_preserves_decoded_samples_despite_asf_timebase() =>
        VerifyWmaAlac(ConversionMode.Alac, true, 0);

    [EngineFact]
    public Task Wma_alac_output_check_still_succeeds_without_optional_source_check() =>
        VerifyWmaAlac(ConversionMode.Smart, false, 0);

    [EngineFact]
    public Task Wma_nonzero_start_is_normalized_without_inserting_silence_or_losing_samples() =>
        VerifyWmaAlac(ConversionMode.Alac, true, 2.5);

    private static async Task VerifyWmaAlac(ConversionMode mode, bool verifySource, double offset)
    {
        using var folder = new TestFolder();
        var source = Path.Combine(folder.Path, "毫秒时基.wma");
        // ASF uses millisecond timestamps while the decoded samples are at 44.1 kHz.
        // Generate locally; never depend on the user's copyrighted music for regression.
        await ConversionTests.Engine("-v", "error", "-f", "lavfi",
            "-i", "sine=frequency=997:sample_rate=44100:duration=5", "-ac", "2",
            "-c:a", "wmav2", "-b:a", "192k", "-metadata", "title=时间戳回归",
            "-output_ts_offset", offset.ToString(System.Globalization.CultureInfo.InvariantCulture), source);
        var originalBytes = await File.ReadAllBytesAsync(source);
        var options = new ConversionOptions(Path.Combine(folder.Path, "out"), mode, VerifyAudio: verifySource);
        var converter = new MusicConverter();
        var preview = await converter.PreviewAsync([source], options);
        Assert.Equal(PlanAction.EncodeAlac, preview.Files[0].Action);
        var result = Assert.Single(await converter.ConvertAsync(preview.Files, options));
        Assert.True(result.Status == ItemStatus.Succeeded, result.Message);

        var info = await new AudioProbe(EnginePaths.Discover()).ReadAsync(result.OutputPath!);
        Assert.Equal("alac", info.CodecName);
        Assert.Equal(44100, info.SampleRate);
        Assert.Equal(2, info.Channels);
        Assert.Equal(24, info.BitsPerSample);
        Assert.Equal("时间戳回归", info.Tags["title"]);
        var sourcePcm = Path.Combine(folder.Path, "source.pcm");
        var outputPcm = Path.Combine(folder.Path, "output.pcm");
        await ConversionTests.Engine("-v", "error", "-xerror", "-i", source,
            "-map", "0:a:0", "-c:a", "pcm_s24le", "-f", "s24le", sourcePcm);
        await ConversionTests.Engine("-v", "error", "-xerror", "-i", result.OutputPath!,
            "-map", "0:a:0", "-c:a", "pcm_s24le", "-f", "s24le", outputPcm);
        var decoded = await File.ReadAllBytesAsync(outputPcm);
        Assert.NotEmpty(decoded);
        Assert.Equal(await File.ReadAllBytesAsync(sourcePcm), decoded);
        Assert.NotNull(info.DeclaredSamples);
        Assert.InRange(Math.Abs(info.DeclaredSamples.Value - decoded.LongLength / (2 * 3)), 0, 2);
        var probe = await ProcessRunner.RunAsync(EnginePaths.Discover().Ffprobe,
            ["-v", "error", "-select_streams", "a:0", "-show_entries", "stream=start_time",
             "-of", "json", result.OutputPath!], TimeSpan.FromSeconds(10));
        using var parsed = System.Text.Json.JsonDocument.Parse(probe.StandardOutput);
        var start = parsed.RootElement.GetProperty("streams")[0].GetProperty("start_time").GetString()!;
        Assert.Equal(0, double.Parse(start, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(source));
        Assert.Empty(Directory.GetFiles(options.OutputDirectory, ".mfc-*", SearchOption.AllDirectories));
    }

    [EngineFact]
    public async Task Raw_aac_is_remuxed_without_pcm_changes()
    {
        using var folder = new TestFolder();
        var song = await ConversionTests.Generate(folder, "原始.aac", "-c:a", "aac", "-b:a", "192k", "-f", "adts");
        var options = new ConversionOptions(Path.Combine(folder.Path, "out"));
        var converter = new MusicConverter();
        var preview = await converter.PreviewAsync([song], options);
        Assert.Equal(PlanAction.Remux, preview.Files[0].Action);
        var result = (await converter.ConvertAsync(preview.Files, options))[0];
        Assert.True(result.Status == ItemStatus.Succeeded, result.Message);
    }

    [EngineFact]
    public async Task Opus_to_alac_checks_duration_despite_codec_preskip()
    {
        using var folder = new TestFolder();
        var song = ConversionTests.Fixture(folder, "sine-997hz.opus", "opus.ogg");
        var options = new ConversionOptions(Path.Combine(folder.Path, "out"));
        var converter = new MusicConverter();
        var preview = await converter.PreviewAsync([song], options);
        Assert.Equal("opus", preview.Files[0].Audio!.CodecName);
        Assert.InRange(preview.Files[0].Audio!.DurationSeconds, .49, .55);
        var result = (await converter.ConvertAsync(preview.Files, options))[0];
        Assert.True(result.Status == ItemStatus.Succeeded, result.Message);
        Assert.Contains("不提升", result.Message);
    }

    [EngineFact]
    public async Task Opus_stream_tags_are_written_as_mp4_container_tags()
    {
        using var folder = new TestFolder();
        var song = ConversionTests.Fixture(folder, "sine-997hz.opus", "tags.ogg");
        var options = new ConversionOptions(Path.Combine(folder.Path, "out"));
        var converter = new MusicConverter();
        var preview = await converter.PreviewAsync([song], options);
        var expectedTags = new Dictionary<string, string>
        {
            ["title"] = "中文标题", ["artist"] = "测试歌手", ["album_artist"] = "专辑歌手",
            ["track"] = "2/9", ["disc"] = "1/2"
        };
        foreach (var (key, expected) in expectedTags)
            Assert.Equal(expected, preview.Files[0].Audio!.Tags.GetValueOrDefault(key));
        var result = (await converter.ConvertAsync(preview.Files, options))[0];
        Assert.True(result.Status == ItemStatus.Succeeded, result.Message);
        var info = await new AudioProbe(EnginePaths.Discover()).ReadAsync(result.OutputPath!);
        foreach (var key in new[] { "title", "artist", "album_artist", "track", "disc" })
            Assert.Equal(preview.Files[0].Audio!.Tags[key], info.Tags.GetValueOrDefault(key));
    }

    [EngineFact]
    public async Task Frame_boundary_truncated_flac_is_not_success()
    {
        using var folder = new TestFolder();
        var song = Path.Combine(folder.Path, "truncated.flac");
        await ConversionTests.Engine("-v", "error", "-f", "lavfi", "-i", "sine=duration=10:sample_rate=48000",
            "-c:a", "flac", song);
        // Preserve declared total sample count, remove complete audio frames after a known packet boundary.
        var packets = await ProcessRunner.RunAsync(EnginePaths.Discover().Ffprobe,
            ["-v", "error", "-show_packets", "-select_streams", "a:0", "-of", "json", song], TimeSpan.FromSeconds(10));
        using var parsed = System.Text.Json.JsonDocument.Parse(packets.StandardOutput);
        var packet = parsed.RootElement.GetProperty("packets").EnumerateArray().First(
            p => double.Parse(p.GetProperty("pts_time").GetString()!, System.Globalization.CultureInfo.InvariantCulture) >= 5);
        var boundary = long.Parse(packet.GetProperty("pos").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        using (var file = new FileStream(song, FileMode.Open, FileAccess.Write)) file.SetLength(boundary);
        var options = new ConversionOptions(Path.Combine(folder.Path, "out"));
        var converter = new MusicConverter();
        var preview = await converter.PreviewAsync([song], options);
        Assert.NotEqual(PlanAction.Unsupported, preview.Files[0].Action);
        var result = (await converter.ConvertAsync(preview.Files, options))[0];
        Assert.Equal(ItemStatus.Failed, result.Status);
        Assert.Contains("完整", result.Message);
        Assert.Null(result.OutputPath);
        var reportPath = await ReportWriter.WriteAsync(Path.Combine(folder.Path, "reports"), [result]);
        using var report = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
        Assert.Equal(1, report.RootElement.GetProperty("failed").GetInt32());
        Assert.Equal(0, report.RootElement.GetProperty("skipped").GetInt32());
    }

    [EngineFact]
    public async Task Explicit_aac_mode_is_supported_and_output_codec_verified()
    {
        using var folder = new TestFolder();
        var song = await ConversionTests.Generate(folder, "audio.flac", "-c:a", "flac");
        var options = new ConversionOptions(Path.Combine(folder.Path, "out"), ConversionMode.Aac);
        var converter = new MusicConverter();
        var preview = await converter.PreviewAsync([song], options);
        var result = (await converter.ConvertAsync(preview.Files, options))[0];
        Assert.True(result.Status == ItemStatus.Succeeded, result.Message);
        Assert.Equal("aac", (await new AudioProbe(EnginePaths.Discover()).ReadAsync(result.OutputPath!)).CodecName);
    }

    [EngineFact]
    public async Task Flac_embedded_cover_survives_in_m4a()
    {
        using var folder = new TestFolder();
        var wav = await ConversionTests.Generate(folder, "source.wav", "-c:a", "pcm_s16le");
        var song = Path.Combine(folder.Path, "cover.flac");
        var png = Path.Combine(folder.Path, "cover.png");
        await ConversionTests.Engine("-v", "error", "-f", "lavfi", "-i", "color=red:size=64x64", "-frames:v", "1", png);
        await ConversionTests.Engine("-v", "error", "-i", wav, "-i", png,
            "-map", "0:a", "-map", "1:v", "-c:a", "flac", "-c:v", "copy", "-disposition:v", "attached_pic", song);
        var options = new ConversionOptions(Path.Combine(folder.Path, "out"));
        var converter = new MusicConverter();
        var preview = await converter.PreviewAsync([song], options);
        Assert.NotNull(preview.Files[0].Audio?.CoverStreamIndex);
        var result = (await converter.ConvertAsync(preview.Files, options))[0];
        Assert.True(result.Status == ItemStatus.Succeeded, result.Message);
        Assert.NotNull((await new AudioProbe(EnginePaths.Discover()).ReadAsync(result.OutputPath!)).CoverStreamIndex);
    }

    [EngineFact]
    public async Task Empty_audio_payload_is_not_a_successful_copy()
    {
        using var folder = new TestFolder();
        var source = await ConversionTests.Generate(folder, "empty.wav", "-t", "0", "-c:a", "pcm_s16le");
        var converter = new MusicConverter();
        var options = new ConversionOptions(Path.Combine(folder.Path, "out"));
        var plan = await converter.PreviewAsync([source], options);
        var results = await converter.ConvertAsync(plan.Files, options);
        Assert.Equal(ItemStatus.Failed, results[0].Status);
    }

    [EngineFact]
    public async Task Cancelling_active_conversion_cleans_only_owned_temporary_file()
    {
        using var folder = new TestFolder();
        var song = await ConversionTests.Generate(folder, "cancel.flac", "-c:a", "flac");
        var keep = folder.File("out/.mfc-someone-elses.part", "keep");
        var options = new ConversionOptions(Path.Combine(folder.Path, "out"));
        var converter = new MusicConverter();
        var preview = await converter.PreviewAsync([song], options);
        using var cts = new CancellationTokenSource();
        var result = (await converter.ConvertAsync(preview.Files, options, new DirectProgress<BatchProgress>(p =>
        {
            if (p.Message == "检查源音频完整性") cts.Cancel();
        }), cts.Token))[0];
        Assert.Equal(ItemStatus.Cancelled, result.Status);
        Assert.Equal("keep", await File.ReadAllTextAsync(keep));
        Assert.Single(Directory.GetFiles(options.OutputDirectory));
    }

    [EngineFact]
    public async Task Slow_child_process_timeout_is_bounded()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() => ProcessRunner.RunAsync(EnginePaths.Discover().Ffmpeg,
            ["-nostdin", "-v", "error", "-re", "-f", "lavfi", "-i", "sine=duration=60", "-f", "null", "-"],
            TimeSpan.FromMilliseconds(400)));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
    }

    private sealed class DirectProgress<T>(Action<T> action) : IProgress<T>
    { public void Report(T value) => action(value); }
}
