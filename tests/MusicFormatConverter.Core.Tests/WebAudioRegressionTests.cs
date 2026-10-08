using System.Text.Json;

namespace MusicFormatConverter.Core.Tests;

public class WebAudioRegressionTests
{
    [EngineFact]
    public async Task Mono_aac_retains_channel_count_and_rate()
    {
        using var folder = new TestFolder();
        var song = await ConversionTests.Generate(folder, "mono.aif", "-ar", "44100", "-ac", "1", "-c:a", "pcm_s16be");
        var options = new ConversionOptions(Path.Combine(folder.Path, "out"), ConversionMode.Aac);
        var converter = new MusicConverter();
        var preview = await converter.PreviewAsync([song], options);
        var result = Assert.Single(await converter.ConvertAsync(preview.Files, options));
        Assert.True(result.Status == ItemStatus.Succeeded, result.Message);
        var audio = await new AudioProbe(EnginePaths.Discover()).ReadAsync(result.OutputPath!);
        Assert.Equal("aac", audio.CodecName);
        Assert.Equal(44100, audio.SampleRate);
        Assert.Equal(1, audio.Channels);
    }

    [EngineFact]
    public async Task Cancellation_during_real_encoder_write_waits_for_exit_and_removes_owned_temp()
    {
        using var folder = new TestFolder();
        var song = Path.Combine(folder.Path, "noise.wav");
        await ConversionTests.Engine("-v", "error", "-f", "lavfi",
            "-i", "anoisesrc=sample_rate=48000:duration=120:seed=42",
            "-ac", "2", "-c:a", "pcm_s16le", song);
        var keep = folder.File("out/.mfc-someone-elses.part", "keep");
        var options = new ConversionOptions(Path.Combine(folder.Path, "out"), ConversionMode.Alac, VerifyAudio: false);
        var converter = new MusicConverter();
        var preview = await converter.PreviewAsync([song], options);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var conversion = converter.ConvertAsync(preview.Files, options, cancellationToken: cts.Token);
        var triggered = false;
        while (!conversion.IsCompleted && !cts.IsCancellationRequested)
        {
            if (Directory.EnumerateFiles(options.OutputDirectory, "*.part")
                .Any(p => p != keep && new FileInfo(p).Length > 0))
            {
                triggered = true;
                cts.Cancel();
                break;
            }
            await Task.Delay(2);
        }
        var result = Assert.Single(await conversion);
        Assert.True(triggered, "必须在真实临时文件写入后取消，而不是预先取消或自然完成。");
        Assert.Equal(ItemStatus.Cancelled, result.Status);
        Assert.Null(result.OutputPath);
        Assert.Contains("未保留临时结果", result.Message);
        Assert.Equal(keep, Assert.Single(Directory.GetFiles(options.OutputDirectory)));
        Assert.Equal("keep", await File.ReadAllTextAsync(keep));
    }

    [EngineFact]
    public async Task Aac_from_32khz_asf_uses_continuous_sample_timestamps()
    {
        using var folder = new TestFolder();
        var song = Path.Combine(folder.Path, "32khz.wma");
        await ConversionTests.Engine("-v", "error", "-f", "lavfi",
            "-i", "sine=frequency=997:sample_rate=32000:duration=5", "-ac", "2",
            "-c:a", "wmav2", "-b:a", "128k", song);
        var options = new ConversionOptions(Path.Combine(folder.Path, "out"), ConversionMode.Aac);
        var converter = new MusicConverter();
        var preview = await converter.PreviewAsync([song], options);
        var result = Assert.Single(await converter.ConvertAsync(preview.Files, options));
        Assert.True(result.Status == ItemStatus.Succeeded, result.Message);
        var audio = await new AudioProbe(EnginePaths.Discover()).ReadAsync(result.OutputPath!);
        Assert.Equal("aac", audio.CodecName);
        Assert.Equal(32000, audio.SampleRate);
        Assert.Equal(2, audio.Channels);
    }

    [EngineFact]
    public Task Cancellation_retries_transient_owned_temp_lock() => VerifyLockedCancellation(false);

    [EngineFact]
    public Task Cancellation_retains_permanent_cleanup_warning_in_result_and_reports() => VerifyLockedCancellation(true);

    private static async Task VerifyLockedCancellation(bool holdUntilResult)
    {
        // Windows share flags prevent deletion. Unix unlink semantics intentionally differ.
        if (!OperatingSystem.IsWindows()) return;
        using var folder = new TestFolder();
        var song = await ConversionTests.Generate(folder, "cancel.flac", "-c:a", "flac");
        var keep = folder.File("out/.mfc-someone-elses.part", "keep");
        var options = new ConversionOptions(Path.Combine(folder.Path, "out"), ConversionMode.Alac);
        var converter = new MusicConverter();
        var preview = await converter.PreviewAsync([song], options);
        using var cts = new CancellationTokenSource();
        FileStream? blocker = null;
        string? owned = null;
        Task release = Task.CompletedTask;
        BatchProgress? final = null;
        ConversionResult result;
        try
        {
            result = Assert.Single(await converter.ConvertAsync(preview.Files, options,
                new DirectProgress(p =>
                {
                    if (p.Result != null) final = p;
                    if (p.Message != "校验输出音频与标签") return;
                    owned = Assert.Single(Directory.GetFiles(options.OutputDirectory, "*.part"), p => p != keep);
                    blocker = new FileStream(owned, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    if (!holdUntilResult)
                        release = Task.Run(async () => { await Task.Delay(350); blocker.Dispose(); });
                    cts.Cancel();
                }), cts.Token));
            await release;
            Assert.NotNull(owned);
            Assert.Equal(ItemStatus.Cancelled, result.Status);
            Assert.Null(result.OutputPath);
            Assert.Equal(result, final!.Result);
            Assert.Equal("keep", await File.ReadAllTextAsync(keep));
            Assert.DoesNotContain(Directory.GetFiles(options.OutputDirectory), p => !p.EndsWith(".part"));
            if (holdUntilResult)
            {
                Assert.True(File.Exists(owned));
                Assert.Contains("临时文件清理失败", result.Message);
                Assert.Contains(owned, result.Message);
                Assert.DoesNotContain("未保留临时结果", result.Message);
                var report = await ReportWriter.WriteAsync(Path.Combine(folder.Path, "report"), [result]);
                using var json = JsonDocument.Parse(await File.ReadAllTextAsync(report));
                Assert.Contains("临时文件清理失败", json.RootElement.ToString());
                Assert.Contains("临时文件清理失败", await File.ReadAllTextAsync(Path.ChangeExtension(report, ".csv")));
            }
            else
            {
                Assert.False(File.Exists(owned));
                Assert.DoesNotContain("清理失败", result.Message);
                Assert.Contains("未保留临时结果", result.Message);
            }
        }
        finally
        {
            await release;
            blocker?.Dispose();
        }
    }

    private sealed class DirectProgress(Action<BatchProgress> action) : IProgress<BatchProgress>
    {
        public void Report(BatchProgress value) => action(value);
    }
}
