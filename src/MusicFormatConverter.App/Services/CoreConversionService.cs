using MusicFormatConverter.Core;

namespace MusicFormatConverter.App.Services;

/// <summary>Core is the only authority for file scanning, policy, output safety and process cleanup.</summary>
public sealed class CoreConversionService : IConversionService
{
    private readonly MusicConverter _converter = new();

    public Task<ScanResult> ScanAsync(IEnumerable<string> inputs, string outputDirectory, CancellationToken cancellationToken)
    {
        var snapshot = inputs.ToArray();
        return Task.Run(() => SourceScanner.Scan(snapshot, outputDirectory, cancellationToken), cancellationToken);
    }

    // Keep synchronous scanning/probing setup off the dispatcher as well as the asynchronous work.
    public Task<PreflightResult> PreviewAsync(IEnumerable<string> inputs, ConversionOptions options,
        IProgress<string>? progress, CancellationToken cancellationToken) =>
        Task.Run(() => _converter.PreviewAsync(inputs, options, progress, cancellationToken), cancellationToken);

    public Task<IReadOnlyList<ConversionResult>> ConvertAsync(IEnumerable<PlannedFile> files,
        ConversionOptions options, IProgress<BatchProgress>? progress, CancellationToken cancellationToken) =>
        Task.Run(() => _converter.ConvertAsync(files, options, progress, cancellationToken), cancellationToken);

    public Task<string> WriteReportAsync(string directory, IReadOnlyList<ConversionResult> results,
        CancellationToken cancellationToken) =>
        Task.Run(() => ReportWriter.WriteAsync(directory, results, cancellationToken), cancellationToken);
}
