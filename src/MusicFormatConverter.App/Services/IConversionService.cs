using MusicFormatConverter.Core;

namespace MusicFormatConverter.App.Services;

public interface IConversionService
{
    Task<ScanResult> ScanAsync(IEnumerable<string> inputs, string outputDirectory, CancellationToken cancellationToken);
    Task<PreflightResult> PreviewAsync(IEnumerable<string> inputs, ConversionOptions options,
        IProgress<string>? progress, CancellationToken cancellationToken);
    Task<IReadOnlyList<ConversionResult>> ConvertAsync(IEnumerable<PlannedFile> files,
        ConversionOptions options, IProgress<BatchProgress>? progress, CancellationToken cancellationToken);
    Task<string> WriteReportAsync(string directory, IReadOnlyList<ConversionResult> results,
        CancellationToken cancellationToken);
}
