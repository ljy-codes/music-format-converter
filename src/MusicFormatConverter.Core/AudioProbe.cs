using System.Globalization;
using System.Text.Json;

namespace MusicFormatConverter.Core;

public sealed class AudioProbe(EnginePaths engines)
{
    // Container playlists (concat/HLS) and network protocols are intentionally excluded.
    internal const string AllowedFormats = "mov,mp3,flac,wav,aiff,aac,ogg,asf,ape,wv,tta,tak,mpc,mpc8,amr,dsf,iff,matroska,webm";

    public async Task<AudioInfo> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("源文件不存在。", path);
        if (SourceScanner.IsLink(path)) throw new AudioProcessingException("不处理链接文件。");
        if (Path.GetExtension(path).Equals(".cue", StringComparison.OrdinalIgnoreCase))
            throw new AudioProcessingException("CUE 是分轨索引，首版不执行分轨；请添加对应整轨音频。");
        if (new FileInfo(path).Length == 0) throw new AudioProcessingException("文件为空，无法转换。");
        var result = await ProcessRunner.RunAsync(engines.Ffprobe,
            ["-v", "error", "-protocol_whitelist", "file,pipe", "-format_whitelist", AllowedFormats,
             "-probesize", "10000000", "-analyzeduration", "10000000", "-show_streams", "-show_format",
             "-of", "json", Path.GetFullPath(path)], TimeSpan.FromSeconds(45), cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 || !string.IsNullOrWhiteSpace(result.StandardError))
            throw new AudioProcessingException("无法识别：格式不支持、受保护或文件损坏。");
        return Parse(result.StandardOutput, new FileInfo(path).Length);
    }

    public static AudioInfo Parse(string json, long size)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            var streams = root.GetProperty("streams").EnumerateArray().ToArray();
            var audioStreams = streams.Where(s => Text(s, "codec_type") == "audio").ToArray();
            if (audioStreams.Length != 1)
                throw new AudioProcessingException(audioStreams.Length == 0 ? "未检测到可用音频。" : "暂不支持多音轨文件，避免静默丢弃音轨。");
            var audio = audioStreams[0];
            if (Text(audio, "codec_tag_string") is "enca" or "drms")
                throw new AudioProcessingException("文件受保护或已加密，本工具不处理。");
            if (Integer(audio, "sample_rate") <= 0 || Integer(audio, "channels") <= 0 ||
                string.IsNullOrWhiteSpace(Text(audio, "codec_name")))
                throw new AudioProcessingException("无法读取有效音频参数：文件损坏或编码不支持。");
            var videos = streams.Where(s => Text(s, "codec_type") == "video").ToArray();
            if (videos.Any(s => !s.TryGetProperty("disposition", out var d) || Integer(d, "attached_pic") != 1))
                throw new AudioProcessingException("首版不提取视频音轨，请添加独立音频文件。");
            var format = root.GetProperty("format");
            var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            ReadTags(format, tags);
            ReadTags(audio, tags);
            foreach (var (alias, canonical) in new[]
                     { ("TRACKNUMBER", "track"), ("DISCNUMBER", "disc"), ("ALBUMARTIST", "album_artist"), ("YEAR", "date") })
                if (!tags.ContainsKey(canonical) && tags.TryGetValue(alias, out var tagValue)) tags[canonical] = tagValue;
            var bits = Integer(audio, "bits_per_raw_sample");
            if (bits == 0) bits = Integer(audio, "bits_per_sample");
            // Some ASF/WMA Lossless files omit both precision fields. A 16-bit
            // integer decoder output is unambiguous; s32(p) is not (24 or 32 bits).
            // Do not guess precision for other codecs or floating-point output.
            if (bits == 0 && Text(audio, "codec_name") == "wmalossless" &&
                Text(audio, "sample_fmt") is "s16" or "s16p")
                bits = 16;
            var duration = Number(audio, "duration");
            if (duration <= 0) duration = Number(format, "duration");
            if (!double.IsFinite(duration) || duration < 0) duration = 0;
            return new(Text(format, "format_name"), Text(audio, "codec_name"),
                Integer(audio, "sample_rate"), Integer(audio, "channels"), bits,
                Text(audio, "sample_fmt"), duration, size, Integer(audio, "index"),
                videos.Length > 0 ? Integer(videos[0], "index") : null, tags)
            {
                CoverCodec = videos.Length > 0 ? Text(videos[0], "codec_name") : null,
                CoverCount = videos.Length,
                DeclaredSamples = ReadDeclaredSamples(audio, format)
            };
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        { throw new AudioProcessingException("探测结果无效，无法可靠识别音频。"); }
    }

    private static long? ReadDeclaredSamples(JsonElement audio, JsonElement format)
    {
        // Use only lossless containers with an authoritative sample count. MP3/AAC/OGG
        // container durations can be estimates or include encoder delay and are not used here.
        var codec = Text(audio, "codec_name");
        var container = Text(format, "format_name").Split(',');
        var reliable = codec == "alac" || codec == "flac" ||
                       codec.StartsWith("pcm_", StringComparison.Ordinal) &&
                       container.Any(c => c is "wav" or "aiff");
        var rate = Integer(audio, "sample_rate");
        if (!reliable || rate <= 0 || Text(audio, "time_base") != $"1/{rate}") return null;
        return long.TryParse(Text(audio, "duration_ts"), CultureInfo.InvariantCulture, out var samples) && samples > 0
            ? samples : null;
    }

    private static void ReadTags(JsonElement element, Dictionary<string, string> tags)
    {
        if (!element.TryGetProperty("tags", out var source) || source.ValueKind != JsonValueKind.Object) return;
        foreach (var property in source.EnumerateObject())
            tags[property.Name] = property.Value.ToString();
    }

    private static string Text(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) ? property.ToString() : "";
    private static int Integer(JsonElement value, string name) =>
        int.TryParse(Text(value, name), CultureInfo.InvariantCulture, out var result) ? result : 0;
    private static double Number(JsonElement value, string name) =>
        double.TryParse(Text(value, name), CultureInfo.InvariantCulture, out var result) ? result : 0;
}
