using System.Globalization;

namespace MusicFormatConverter.Core;

internal sealed record AudioCheck(string Hash, double DurationSeconds);

internal sealed class FfmpegAudio(EnginePaths engines)
{
    private static List<string> InputArguments(string input) =>
    [
        "-hide_banner", "-nostdin", "-v", "error", "-xerror",
        "-protocol_whitelist", "file,pipe", "-format_whitelist", AudioProbe.AllowedFormats,
        "-err_detect", "explode", "-i", Path.GetFullPath(input)
    ];

    public async Task EncodeAsync(PlannedFile plan, ConversionOptions options, string temporary,
        Action<double> progress, CancellationToken cancellationToken)
    {
        var info = plan.Audio!;
        var arguments = InputArguments(plan.Source.FullPath);
        arguments.AddRange(["-map", $"0:{info.AudioStreamIndex}", "-map_metadata", options.PreserveMetadata ? "0" : "-1",
            "-map_chapters", "-1", "-sn", "-dn"]);
        if (options.PreserveMetadata)
            foreach (var key in new[] { "title", "artist", "album", "album_artist", "track", "disc", "date", "genre",
                         "composer", "comment", "copyright", "compilation" })
                if (info.Tags.TryGetValue(key, out var value))
                    arguments.AddRange(["-metadata", $"{key}={value}"]);
        if (options.PreserveMetadata && plan.Action != PlanAction.EncodeWav && info.CoverStreamIndex is { } cover)
        {
            // Attached pictures are already finite streams. -frames:v 1 would prematurely
            // stop the entire transcode before buffered audio has been written.
            arguments.AddRange(["-map", $"0:{cover}", "-c:v", info.CoverCodec is "png" or "mjpeg" ? "copy" : "mjpeg",
                "-disposition:v:0", "attached_pic"]);
        }
        else arguments.Add("-vn");

        switch (plan.Action)
        {
            case PlanAction.Remux:
                arguments.AddRange(["-c:a", "copy"]);
                break;
            case PlanAction.EncodeAlac:
                // Standalone audio is a continuous sample sequence, not the source container's
                // rounded timestamp grid (e.g. ASF milliseconds). Rebase without resampling,
                // inserting silence or dropping samples; retain the strict output checks.
                arguments.AddRange(["-af", "asetpts=N/SR/TB", "-c:a", "alac", "-sample_fmt",
                    AudioPolicy.IsLossy(info) || info.BitsPerSample > 16 ? "s32p" : "s16p"]);
                break;
            case PlanAction.EncodeAac:
                // AAC also needs continuous timestamps for ASF input. TAK may
                // declare only a channel count; name the already-validated layout
                // without changing channel count or requesting a downmix.
                var layout = info.Channels switch
                {
                    1 => "mono",
                    2 => "stereo",
                    _ => throw new AudioProcessingException("AAC 仅支持单声道或双声道，不自动混音。")
                };
                arguments.AddRange(["-af", "asetpts=N/SR/TB", "-c:a", "aac", "-b:a", "256k",
                    "-ar", info.SampleRate.ToString(CultureInfo.InvariantCulture), "-channel_layout", layout]);
                break;
            case PlanAction.EncodeMp3:
                arguments.AddRange(["-af", "asetpts=N/SR/TB", "-c:a", "libmp3lame", "-b:a", "320k",
                    "-ar", info.SampleRate.ToString(CultureInfo.InvariantCulture)]);
                break;
            case PlanAction.EncodeFlac:
                arguments.AddRange(["-af", "asetpts=N/SR/TB", "-c:a", "flac", "-sample_fmt",
                    AudioPolicy.IsLossy(info) || info.BitsPerSample > 16 ? "s32" : "s16"]);
                break;
            case PlanAction.EncodeWav:
                arguments.AddRange(["-af", "asetpts=N/SR/TB", "-c:a",
                    AudioPolicy.IsLossy(info) || info.BitsPerSample > 16 ? "pcm_s24le" : "pcm_s16le"]);
                break;
            default: throw new InvalidOperationException("该处理方案不需要转码。");
        }
        var container = plan.Action switch
        {
            PlanAction.EncodeMp3 => "mp3",
            PlanAction.EncodeFlac => "flac",
            PlanAction.EncodeWav => "wav",
            _ => "ipod"
        };
        if (container == "ipod") arguments.AddRange(["-movflags", "+faststart"]);
        if (container == "mp3") arguments.AddRange(["-id3v2_version", "3", "-write_xing", "1"]);
        if (container == "wav") arguments.AddRange(["-rf64", "auto"]);
        arguments.AddRange(["-progress", "pipe:1", "-stats_period", "0.5",
            "-nostats", "-f", container, "-n", temporary]);
        var result = await ProcessRunner.RunAsync(engines.Ffmpeg, arguments, TimeSpan.FromHours(72),
            cancellationToken, line =>
            {
                if (TryTime(line, out var seconds) && info.DurationSeconds > 0)
                    progress(Math.Clamp(seconds / info.DurationSeconds, 0, 1));
            }, TimeSpan.FromMinutes(3)).ConfigureAwait(false);
        EnsureSuccess(result);
    }

    public async Task<AudioCheck> CheckAsync(string input, int streamIndex, Action<double>? progress,
        CancellationToken cancellationToken)
    {
        var arguments = InputArguments(input);
        arguments.AddRange(["-map", $"0:{streamIndex}", "-vn", "-sn", "-dn", "-af", "asetpts=N/SR/TB", "-c:a", "pcm_s32le",
            "-progress", "pipe:1", "-stats_period", "0.5", "-nostats", "-f", "hash", "-hash", "sha256", "-"]);
        string? hash = null;
        double duration = 0;
        var result = await ProcessRunner.RunAsync(engines.Ffmpeg, arguments, TimeSpan.FromHours(72),
            cancellationToken, line =>
            {
                if (line.StartsWith("SHA256=", StringComparison.Ordinal)) hash = line[7..].Trim();
                if (TryTime(line, out var seconds))
                {
                    duration = Math.Max(duration, seconds);
                    progress?.Invoke(duration);
                }
            }, TimeSpan.FromMinutes(3)).ConfigureAwait(false);
        EnsureSuccess(result);
        if (hash?.Length != 64) throw new AudioProcessingException("无法完成音频完整性检查。");
        if (hash.Equals("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", StringComparison.OrdinalIgnoreCase))
            throw new AudioProcessingException("文件包含音频头但没有可解码的音频数据。");
        return new(hash, duration);
    }

    private static bool TryTime(string line, out double seconds)
    {
        seconds = 0;
        if (!line.StartsWith("out_time_us=", StringComparison.Ordinal) ||
            !long.TryParse(line.AsSpan(12), CultureInfo.InvariantCulture, out var time)) return false;
        seconds = time / 1_000_000d;
        return true;
    }

    private static void EnsureSuccess(ProcessResult result)
    {
        if (result.ExitCode == 0 && string.IsNullOrWhiteSpace(result.StandardError)) return;
        var error = result.StandardError;
        if (error.Contains("No space left", StringComparison.OrdinalIgnoreCase))
            throw new IOException("输出磁盘空间不足", unchecked((int)0x80070070));
        if (error.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("输出目录或源文件无访问权限。");
        // Do not expose full paths, codec traces, or unbounded stderr as UI text.
        throw new AudioProcessingException("音频处理失败：文件损坏、编码不支持或解码校验异常。");
    }
}
