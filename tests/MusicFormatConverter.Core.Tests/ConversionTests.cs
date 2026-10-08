using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MusicFormatConverter.Core.Tests;

public sealed class EngineFactAttribute : FactAttribute
{
    public EngineFactAttribute()
    {
        try { EnginePaths.Discover(); }
        catch (FileNotFoundException) { Skip = "需要运行脚本准备真实 FFmpeg 引擎；未执行集成验收。"; }
    }
}

public class ConversionTests
{
    private static EnginePaths Engines => EnginePaths.Discover();

    internal static string Fixture(TestFolder folder, string fixtureName, string localName)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureName);
        Assert.True(File.Exists(source), $"缺少固定合成样本：{source}。请检查 Fixtures 的输出复制配置，不应跳过测试。");
        var target = Path.Combine(folder.Path, localName);
        File.Copy(source, target);
        return target;
    }

    [Theory]
    [InlineData("sine-997hz.mp3")]
    [InlineData("sine-997hz.opus")]
    public async Task Fixed_codec_fixtures_match_their_documented_sha256(string name)
    {
        using var folder = new TestFolder();
        var fixture = Fixture(folder, name, name);
        var sums = await File.ReadAllLinesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "SHA256SUMS.txt"));
        var expected = Assert.Single(sums.Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries)),
            parts => parts.Length == 2 && parts[1] == name)[0];
        Assert.Equal(expected, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(fixture))),
            ignoreCase: true);
    }

    internal static async Task<string> Generate(TestFolder folder, string name, params string[] codecArgs)
    {
        var output = Path.Combine(folder.Path, name);
        await Engine(["-v", "error", "-nostdin", "-f", "lavfi", "-i", "sine=frequency=997:sample_rate=48000:duration=0.5",
            "-ac", "2", "-metadata", "title=中文标题", "-metadata", "artist=测试歌手",
            .. codecArgs, output]);
        return output;
    }

    internal static async Task Engine(params string[] args)
    {
        var start = new ProcessStartInfo(Engines.Ffmpeg)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        Assert.True(process.ExitCode == 0, await errorTask);
    }

    [EngineFact]
    public async Task Mixed_batch_converts_good_files_and_isolates_bad_file()
    {
        using var folder = new TestFolder();
        var flac = await Generate(folder, "保真_.flac", "-c:a", "flac", "-sample_fmt", "s32");
        var mp3 = Fixture(folder, "sine-997hz.mp3", "不重编.mp3");
        var bad = folder.File("坏文件.flac", "invalid");
        var options = new ConversionOptions(Path.Combine(folder.Path, "out"));
        var converter = new MusicConverter();
        var preview = await converter.PreviewAsync([flac, bad, mp3], options);
        Assert.Equal(PlanAction.Copy, preview.Files[2].Action);
        Assert.Equal("mp3", preview.Files[2].Audio!.CodecName);
        Assert.False(Directory.Exists(options.OutputDirectory)); // dry-run creates no audio directories
        var results = await converter.ConvertAsync(preview.Files, options);
        Assert.Equal(3, results.Count);
        Assert.Equal(ItemStatus.Succeeded, results[0].Status);
        Assert.Equal(ItemStatus.Skipped, results[1].Status);
        Assert.Equal(ItemStatus.Succeeded, results[2].Status);
        Assert.Equal(await File.ReadAllBytesAsync(mp3), await File.ReadAllBytesAsync(results[2].OutputPath!));
        Assert.True(File.Exists(flac));
        Assert.Equal("中文标题", (await new AudioProbe(Engines).ReadAsync(results[0].OutputPath!)).Tags["title"]);
        Assert.Empty(Directory.GetFiles(options.OutputDirectory, ".mfc-*", SearchOption.AllDirectories));
        var report = await ReportWriter.WriteAsync(options.OutputDirectory, results);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(report));
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [EngineFact]
    public async Task Alac_conversion_preserves_24bit_pcm_exactly()
    {
        using var folder = new TestFolder();
        var raw = new byte[4800 * 2 * 3];
        new Random(42).NextBytes(raw);
        var rawPath = Path.Combine(folder.Path, "24bit.pcm");
        await File.WriteAllBytesAsync(rawPath, raw);
        var flac = Path.Combine(folder.Path, "24bit.flac");
        await Engine("-v", "error", "-f", "s24le", "-ar", "48000", "-ac", "2", "-i", rawPath, "-c:a", "flac", flac);
        var options = new ConversionOptions(Path.Combine(folder.Path, "out"));
        var converter = new MusicConverter();
        var preview = await converter.PreviewAsync([flac], options);
        var results = await converter.ConvertAsync(preview.Files, options);
        Assert.True(results[0].Status == ItemStatus.Succeeded, results[0].Message);
        var decoded = Path.Combine(folder.Path, "decoded.pcm");
        await Engine("-v", "error", "-i", results[0].OutputPath!, "-c:a", "pcm_s24le", "-f", "s24le", decoded);
        Assert.Equal(raw, await File.ReadAllBytesAsync(decoded));
    }

    [EngineFact]
    public async Task No_overwrite_and_output_can_be_copied_on_next_smart_run()
    {
        using var folder = new TestFolder();
        var song = await Generate(folder, "曲目.flac", "-c:a", "flac");
        var options = new ConversionOptions(Path.Combine(folder.Path, "out"));
        var converter = new MusicConverter();
        var preview = await converter.PreviewAsync([song], options);
        var first = (await converter.ConvertAsync(preview.Files, options))[0];
        var second = (await converter.ConvertAsync(preview.Files, options))[0];
        Assert.True(first.Status == ItemStatus.Succeeded, first.Message);
        Assert.True(second.Status == ItemStatus.Succeeded, second.Message);
        Assert.NotEqual(first.OutputPath, second.OutputPath);
        var again = await converter.PreviewAsync([first.OutputPath!], options with { OutputDirectory = Path.Combine(folder.Path, "next") });
        Assert.Equal(PlanAction.Copy, again.Files[0].Action);
    }

    [EngineFact]
    public async Task Cancelled_queue_produces_no_formal_output()
    {
        using var folder = new TestFolder();
        var song = await Generate(folder, "音乐.flac", "-c:a", "flac");
        var options = new ConversionOptions(Path.Combine(folder.Path, "out"));
        var converter = new MusicConverter();
        var preview = await converter.PreviewAsync([song], options);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var results = await converter.ConvertAsync(preview.Files, options, cancellationToken: cts.Token);
        Assert.All(results, r => Assert.Equal(ItemStatus.Cancelled, r.Status));
        Assert.False(Directory.Exists(options.OutputDirectory) &&
                     Directory.EnumerateFiles(options.OutputDirectory, "*", SearchOption.AllDirectories).Any());
    }

    [EngineFact]
    public async Task Source_changed_after_preview_is_rejected()
    {
        using var folder = new TestFolder();
        var song = await Generate(folder, "音乐.flac", "-c:a", "flac");
        var options = new ConversionOptions(Path.Combine(folder.Path, "out"));
        var converter = new MusicConverter();
        var preview = await converter.PreviewAsync([song], options);
        await File.AppendAllTextAsync(song, "modified");
        var results = await converter.ConvertAsync(preview.Files, options);
        Assert.Equal(ItemStatus.Failed, results[0].Status);
        Assert.Contains("变化", results[0].Message);
    }

    [Fact]
    public async Task Csv_is_bom_encoded_and_protects_formula_cells()
    {
        using var folder = new TestFolder();
        var plan = new PlannedFile(new("=CMD", "=CMD"), null, PlanAction.Unsupported, "=1+1", "", 0);
        var path = await ReportWriter.WriteAsync(folder.Path, [new(plan, ItemStatus.Failed, null, "@bad", null, 0)]);
        var csv = Path.ChangeExtension(path, ".csv");
        var bytes = await File.ReadAllBytesAsync(csv);
        Assert.True(bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
        Assert.Contains("'=CMD", await File.ReadAllTextAsync(csv));
        Assert.Contains("'@bad", await File.ReadAllTextAsync(csv));
    }
}
