namespace MusicFormatConverter.Core;

public static class AudioPolicy
{
    public const string RuleVersion = "2026-10-08.2";
    private static readonly HashSet<int> SampleRates =
        [8000, 11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000, 64000, 88200, 96000, 176400, 192000];
    private static readonly HashSet<string> Lossy =
    [
        "mp1", "mp2", "mp3", "aac", "aac_latm", "vorbis", "opus",
        "wmav1", "wmav2", "wmapro", "wmavoice", "musepack7", "musepack8",
        "ac3", "eac3", "amr_nb", "amr_wb", "atrac1", "atrac3", "atrac3p",
        "gsm", "gsm_ms", "speex", "pcm_mulaw", "pcm_alaw"
    ];

    public static PlannedFile Plan(SourceFile source, AudioInfo audio, ConversionMode mode)
    {
        PlannedFile Reject(string reason) => new(source, audio, PlanAction.Unsupported, reason, "", 0);
        if (!Enum.IsDefined(mode)) return Reject("转换模式无效。");
        if (audio.Channels is < 1 or > 2)
            return Reject("暂不支持该声道配置；不会自动下混。");
        if (!SampleRates.Contains(audio.SampleRate))
            return Reject("采样率不在首版支持范围内；不会自动重采样。");
        if (audio.CodecName.StartsWith("dsd", StringComparison.OrdinalIgnoreCase))
            return Reject("首版不支持 DSD 转换。");

        var lossy = Lossy.Contains(audio.CodecName);
        if (!lossy && (audio.BitsPerSample is < 1 or > 24 ||
                       audio.SampleFormat.StartsWith("flt", StringComparison.Ordinal) ||
                       audio.SampleFormat.StartsWith("dbl", StringComparison.Ordinal)))
            return Reject("当前转换范围不支持该位深或浮点精度，不自动降低精度。");

        var formats = audio.FormatName.Split(',').ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (mode is ConversionMode.Mp3 or ConversionMode.Flac or ConversionMode.Wav)
        {
            var extension = mode == ConversionMode.Mp3 ? ".mp3" : mode == ConversionMode.Flac ? ".flac" : ".wav";
            var alreadyTarget = mode switch
            {
                ConversionMode.Mp3 => formats.Contains("mp3") && audio.CodecName == "mp3",
                ConversionMode.Flac => formats.Contains("flac") && audio.CodecName == "flac",
                _ => formats.Contains("wav") && audio.CodecName is "pcm_u8" or "pcm_s16le" or "pcm_s24le"
            };
            if (alreadyTarget)
                return new(source, audio, PlanAction.Copy, "已是目标格式：原样复制，不为统一参数重复编码。", extension, audio.FileSize);
            if (mode == ConversionMode.Mp3)
            {
                if (audio.SampleRate is not (32000 or 44100 or 48000))
                    return Reject("MP3 320 kbps 模式仅支持 32/44.1/48 kHz 采样率；不会自动重采样，请改用智能保真、FLAC 或 WAV。");
                return new(source, audio, PlanAction.EncodeMp3, "MP3 320 kbps 有损编码（用户指定），保持采样率与声道。",
                    extension, AddHeadroom((long)(Math.Max(0, audio.DurationSeconds) * 40000)));
            }
            var pcmBytes = Math.Max(0, audio.DurationSeconds) * audio.SampleRate * audio.Channels *
                           (lossy || audio.BitsPerSample > 16 ? 3 : 2);
            var name = mode == ConversionMode.Flac ? "FLAC" : "WAV";
            return new(source, audio, mode == ConversionMode.Flac ? PlanAction.EncodeFlac : PlanAction.EncodeWav,
                (lossy ? $"转为 {name}（24 位），不提升有损来源音质，体积可能增大。" : $"转为 {name}，保留采样率、声道和支持的整数精度。") +
                (mode == ConversionMode.Wav ? " WAV 不嵌封面，部分标签可能无法保留。" : ""),
                extension, AddHeadroom((long)Math.Min(pcmBytes, long.MaxValue / 2d)));
        }
        var mp4 = formats.Contains("mov") || formats.Contains("mp4") || formats.Contains("m4a");
        string? compatibleExtension = null;
        if (formats.Contains("mp3") && audio.CodecName == "mp3") compatibleExtension = ".mp3";
        else if (mp4 && audio.CodecName is "aac" or "alac") compatibleExtension = ".m4a";
        else if (formats.Contains("wav") && audio.CodecName is "pcm_u8" or "pcm_s16le" or "pcm_s24le")
            compatibleExtension = ".wav";
        else if (formats.Contains("aiff") && audio.CodecName is "pcm_s8" or "pcm_s16be" or "pcm_s24be")
            compatibleExtension = ".aiff";

        if (compatibleExtension != null &&
            (mode == ConversionMode.Smart ||
             mode == ConversionMode.Alac && audio.CodecName == "alac" ||
             mode == ConversionMode.Aac && audio.CodecName == "aac"))
            return new(source, audio, PlanAction.Copy, "兼容候选：原样复制，音频与标签字节不变。",
                compatibleExtension, audio.FileSize);

        if (audio.CodecName == "aac" && mode != ConversionMode.Alac)
            return new(source, audio, PlanAction.Remux, "AAC 音频不重编码，仅更换为 M4A 封装。",
                ".m4a", AddHeadroom(audio.FileSize));

        if (mode == ConversionMode.Aac)
        {
            if (audio.SampleRate > 96000)
                return Reject("AAC 模式不支持此采样率；请使用智能保真或 ALAC，不自动降采样。");
            return new(source, audio, PlanAction.EncodeAac, "AAC 256 kbps 有损编码（用户指定），不改变采样率。",
                ".m4a", AddHeadroom((long)(Math.Max(0, audio.DurationSeconds) * 32000)));
        }
        var bytes = Math.Max(0, audio.DurationSeconds) * audio.SampleRate * audio.Channels *
                    (lossy || audio.BitsPerSample > 16 ? 3 : 2);
        return new(source, audio, PlanAction.EncodeAlac,
            lossy ? "解码为 24 位 PCM 后编码 ALAC；不提升原音质，体积可能增大。"
                  : "保留采样率、声道和有效位深，转换为 ALAC。",
            ".m4a", AddHeadroom((long)Math.Min(bytes, long.MaxValue / 2d)));
    }

    public static bool IsLossy(AudioInfo audio) => Lossy.Contains(audio.CodecName);
    internal static bool IsLosslessEncoding(PlanAction action) =>
        action is PlanAction.EncodeAlac or PlanAction.EncodeFlac or PlanAction.EncodeWav;
    internal static bool IsLossyEncoding(PlanAction action) =>
        action is PlanAction.EncodeAac or PlanAction.EncodeMp3;
    private static long AddHeadroom(long bytes) =>
        (long)Math.Min(long.MaxValue / 2d, Math.Max(0, bytes) * 1.08 + 2 * 1024 * 1024);
}
