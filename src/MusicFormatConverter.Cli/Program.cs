using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using MusicFormatConverter.Core;

namespace MusicFormatConverter.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            Console.WriteLine("""
                音乐格式转换器 0.1.1 — 完全离线，不修改源文件

                mfc preview --output <目录> [选项] <文件或文件夹...>
                mfc convert --output <目录> [选项] <文件或文件夹...>

                --mode smart|alac|aac|mp3|flac|wav
                                       默认 Apple Music 智能保真；可选 ALAC/AAC/MP3/FLAC/WAV
                                       AAC 256k / MP3 320k 为有损；已有同格式优先复制
                --flat                 不保留来源目录结构
                --skip-source-check    不独立完整解码源文件（转码结果仍校验）
                --strip-metadata       转码时不保留标签与封面；原样复制不改字节
                --report <目录>        转换后输出 JSON/UTF-8 BOM CSV（含本地路径）
                --                     后续均为路径，可包含以 - 开头的文件名

                preview 仅扫描探测，标准输出为 JSON，不生成音频。
                引擎：完整包自带；开发版可用 MFC_ENGINE_DIR 指定本地引擎目录。
                退出码：0全部成功；1未全部处理（有失败/跳过）；2参数或全局错误；130取消。
                """);
            return 0;
        }
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            var parsed = CliArguments.Parse(args);
            var converter = new MusicConverter();
            var serializerOptions = new JsonSerializerOptions
            {
                WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, Converters = { new JsonStringEnumConverter() }
            };
            var preview = await converter.PreviewAsync(parsed.Inputs, parsed.Options,
                new DirectProgress<string>(message => Console.Error.WriteLine(message)), cancellation.Token);
            if (parsed.Command == "preview")
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                { schemaVersion = 1, ruleVersion = AudioPolicy.RuleVersion, preview }, serializerOptions));
                return preview.Files.Count == 0 || preview.Files.Any(p => p.Action == PlanAction.Unsupported) ? 1 : 0;
            }
            foreach (var warning in preview.Warnings) Console.Error.WriteLine("提示：" + warning);
            if (preview.Files.Count == 0) { Console.Error.WriteLine("没有可处理文件。"); return 1; }
            var results = await converter.ConvertAsync(preview.Files, parsed.Options,
                new DirectProgress<BatchProgress>(p =>
                {
                    if (p.Result != null)
                        Console.Error.WriteLine($"[{p.Index}/{p.Total}] {Path.GetFileName(p.SourcePath)}：{p.Result.Status} · {p.Message}");
                }), cancellation.Token);
            Console.WriteLine(JsonSerializer.Serialize(results, serializerOptions));
            if (parsed.ReportDirectory != null)
            {
                try
                {
                    Console.Error.WriteLine("报告：" + await ReportWriter.WriteAsync(parsed.ReportDirectory, results));
                }
                catch (Exception ex) { Console.Error.WriteLine("音频结果不受影响，但报告导出失败：" + ErrorMessages.Summary(ex)); return 2; }
            }
            if (results.Any(r => r.Status == ItemStatus.Cancelled)) return 130;
            return results.Any(r => r.Status is ItemStatus.Failed or ItemStatus.Skipped) ? 1 : 0;
        }
        catch (OperationCanceledException) { Console.Error.WriteLine("已取消。"); return 130; }
        catch (Exception ex) { Console.Error.WriteLine(ErrorMessages.Summary(ex)); return 2; }
        finally { Console.CancelKeyPress -= handler; }
    }

    private sealed class DirectProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
