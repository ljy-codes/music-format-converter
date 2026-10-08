using MusicFormatConverter.Core;

namespace MusicFormatConverter.Cli;

public sealed record CliArguments(string Command, string[] Inputs, ConversionOptions Options, string? ReportDirectory)
{
    public static CliArguments Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("preview" or "convert"))
            throw new ArgumentException("命令必须是 preview 或 convert。");
        string? output = null, report = null;
        var mode = ConversionMode.Smart;
        var preserve = true;
        var verify = true;
        var metadata = true;
        var inputs = new List<string>();
        var literal = false;
        for (var i = 1; i < args.Length; i++)
        {
            if (literal) { inputs.Add(args[i]); continue; }
            string Value()
            {
                if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException("选项缺少值。");
                return args[i];
            }
            switch (args[i])
            {
                case "--": literal = true; break;
                case "--output": output = Value(); break;
                case "--report": report = Value(); break;
                case "--mode":
                    mode = Value().ToLowerInvariant() switch
                    {
                        "smart" => ConversionMode.Smart, "alac" => ConversionMode.Alac, "aac" => ConversionMode.Aac,
                        "mp3" => ConversionMode.Mp3, "flac" => ConversionMode.Flac, "wav" => ConversionMode.Wav,
                        _ => throw new ArgumentException("mode 必须是 smart、alac、aac、mp3、flac 或 wav。")
                    };
                    break;
                case "--flat": preserve = false; break;
                case "--skip-source-check": verify = false; break;
                case "--strip-metadata": metadata = false; break;
                default:
                    if (args[i].StartsWith('-')) throw new ArgumentException($"未知选项：{args[i]}");
                    inputs.Add(args[i]);
                    break;
            }
        }
        if (string.IsNullOrWhiteSpace(output)) throw new ArgumentException("必须指定 --output 输出目录。");
        if (inputs.Count == 0) throw new ArgumentException("请添加至少一个文件或文件夹。");
        return new(args[0], inputs.ToArray(), new(output, mode, preserve, verify, metadata), report);
    }
}
