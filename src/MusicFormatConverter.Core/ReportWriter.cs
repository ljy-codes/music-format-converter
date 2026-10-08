using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MusicFormatConverter.Core;

public static class ReportWriter
{
    public static async Task<string> WriteAsync(string directory, IReadOnlyList<ConversionResult> results,
        CancellationToken cancellationToken = default)
    {
        OutputFiles.EnsureSafeDirectory(directory);
        var name = $"转换报告-{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
        var jsonPath = Path.Combine(directory, name + ".json");
        var csvPath = Path.ChangeExtension(jsonPath, ".csv");
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Converters = { new JsonStringEnumConverter() }
        };
        var content = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            ruleVersion = AudioPolicy.RuleVersion,
            createdAt = DateTimeOffset.Now,
            warning = "本报告包含本地文件路径，请勿向不可信方分享。歌词不作保留承诺。",
            succeeded = results.Count(r => r.Status == ItemStatus.Succeeded),
            failed = results.Count(r => r.Status == ItemStatus.Failed),
            skipped = results.Count(r => r.Status == ItemStatus.Skipped),
            cancelled = results.Count(r => r.Status == ItemStatus.Cancelled),
            results
        }, options);
        await WriteNewAsync(jsonPath, content, cancellationToken).ConfigureAwait(false);
        var csv = new StringBuilder("来源,输出,处理方式,状态,概要原因,SHA256\r\n");
        foreach (var result in results)
            csv.AppendLine(string.Join(',', new[]
            {
                result.Plan.Source.FullPath, result.OutputPath ?? "", result.Plan.Action.ToString(),
                result.Status.ToString(), result.Message, result.Sha256 ?? ""
            }.Select(Csv)));
        await WriteNewAsync(csvPath, csv.ToString(), cancellationToken).ConfigureAwait(false);
        return jsonPath;
    }

    private static string Csv(string value)
    {
        value = value.Replace("\0", "").Replace("\r", " ").Replace("\n", " ");
        var trimmed = value.TrimStart();
        if (trimmed.Length > 0 && "=+-@\t".Contains(trimmed[0])) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static async Task WriteNewAsync(string path, string value, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            65536, FileOptions.Asynchronous);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(true));
        await writer.WriteAsync(value.AsMemory(), cancellationToken).ConfigureAwait(false);
    }
}
