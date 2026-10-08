using System.Text.Json;

namespace MusicFormatConverter.Core.Tests;

public class SkippedStatusTests
{
    private static PlannedFile Unsupported(string path) =>
        new(new(path, Path.GetFileName(path)), null, PlanAction.Unsupported, "暂不支持 8 声道，不自动下混。", "", 0);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unsupported_is_skipped_without_engine_or_output_even_in_cancelled_batch(bool cancelled)
    {
        using var folder = new TestFolder();
        var engines = new EnginePaths(Path.Combine(folder.Path, "missing-ffmpeg"), Path.Combine(folder.Path, "missing-ffprobe"));
        var options = new ConversionOptions(Path.Combine(folder.Path, "out"));
        var plan = Unsupported(Path.Combine(folder.Path, "8ch.m4a"));
        using var cts = new CancellationTokenSource();
        if (cancelled) cts.Cancel();
        var results = await new MusicConverter(engines).ConvertAsync([plan], options, cancellationToken: cts.Token);
        var result = Assert.Single(results);
        Assert.Equal("Skipped", result.Status.ToString());
        Assert.Equal(plan.Reason, result.Message);
        Assert.Null(result.OutputPath);
        Assert.Null(result.Sha256);
        Assert.False(Directory.Exists(options.OutputDirectory));
    }

    [Fact]
    public async Task Missing_audio_in_an_executable_plan_remains_failed()
    {
        using var folder = new TestFolder();
        var engines = new EnginePaths("missing-ffmpeg", "missing-ffprobe");
        var plan = Unsupported(Path.Combine(folder.Path, "bad.flac")) with { Action = PlanAction.EncodeAlac };
        var result = Assert.Single(await new MusicConverter(engines).ConvertAsync([plan],
            new ConversionOptions(Path.Combine(folder.Path, "out"))));
        Assert.Equal(ItemStatus.Failed, result.Status);
    }

    [Fact]
    public async Task Reports_count_skipped_separately_and_serialize_the_status_in_json_and_csv()
    {
        using var folder = new TestFolder();
        var plan = Unsupported(Path.Combine(folder.Path, "8ch.m4a"));
        // Value 5 is intentionally the appended slot; old enum values must remain stable.
        var statuses = new[] { ItemStatus.Succeeded, ItemStatus.Failed, ItemStatus.Cancelled, (ItemStatus)5 };
        var results = statuses.Select(s => new ConversionResult(plan, s, null, "原因", null, 0)).ToArray();
        var report = await ReportWriter.WriteAsync(folder.Path, results);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(report));
        var root = json.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.True(root.TryGetProperty("skipped", out var skipped), "报告需要独立 skipped 计数");
        Assert.Equal(1, skipped.GetInt32());
        foreach (var key in new[] { "succeeded", "failed", "cancelled" })
            Assert.Equal(1, root.GetProperty(key).GetInt32());
        Assert.Equal("Skipped", root.GetProperty("results")[3].GetProperty("status").GetString());
        Assert.Contains("\"Skipped\"", await File.ReadAllTextAsync(Path.ChangeExtension(report, ".csv")));
        Assert.Equal(0, (int)ItemStatus.Ready);
        Assert.Equal(1, (int)ItemStatus.Processing);
        Assert.Equal(2, (int)ItemStatus.Succeeded);
        Assert.Equal(3, (int)ItemStatus.Failed);
        Assert.Equal(4, (int)ItemStatus.Cancelled);
    }
}
