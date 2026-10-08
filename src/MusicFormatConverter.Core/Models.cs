namespace MusicFormatConverter.Core;

public enum ConversionMode { Smart, Alac, Aac, Mp3, Flac, Wav }
// Append values to preserve numeric compatibility with existing callers/reports.
public enum PlanAction { Copy, Remux, EncodeAlac, EncodeAac, Unsupported, EncodeMp3, EncodeFlac, EncodeWav }
// Append terminal statuses; existing numeric values are part of the caller contract.
public enum ItemStatus { Ready, Processing, Succeeded, Failed, Cancelled, Skipped }

public sealed record SourceFile(string FullPath, string RelativePath);

public sealed record AudioInfo(
    string FormatName, string CodecName, int SampleRate, int Channels,
    int BitsPerSample, string SampleFormat, double DurationSeconds, long FileSize,
    int AudioStreamIndex, int? CoverStreamIndex,
    IReadOnlyDictionary<string, string> Tags)
{
    public string? CoverCodec { get; init; }
    public int CoverCount { get; init; }
    public long? DeclaredSamples { get; init; }
}

public sealed record ConversionOptions(
    string OutputDirectory,
    ConversionMode Mode = ConversionMode.Smart,
    bool PreserveFolders = true,
    bool VerifyAudio = true,
    bool PreserveMetadata = true);

public sealed record PlannedFile(
    SourceFile Source, AudioInfo? Audio, PlanAction Action,
    string Reason, string OutputExtension, long EstimatedBytes)
{
    public long SourceWriteTimeUtcTicks { get; init; }
}

public sealed record ScanResult(IReadOnlyList<SourceFile> Files, IReadOnlyList<string> Warnings);

public sealed record PreflightResult(
    IReadOnlyList<PlannedFile> Files, IReadOnlyList<string> Warnings,
    long EstimatedBytes, long? AvailableBytes);

public sealed record ConversionResult(
    PlannedFile Plan, ItemStatus Status, string? OutputPath,
    string Message, string? Sha256, double ElapsedSeconds);

public sealed record BatchProgress(
    int Index, int Total, string SourcePath, double Fraction,
    string Message, ConversionResult? Result = null);
