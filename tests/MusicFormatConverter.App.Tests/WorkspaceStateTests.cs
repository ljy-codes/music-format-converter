using MusicFormatConverter.App.Services;
using MusicFormatConverter.App.ViewModels;
using MusicFormatConverter.Core;

namespace MusicFormatConverter.App.Tests;

public class WorkspaceStateTests
{
    [Fact]
    public async Task Skipped_rows_have_separate_summary_and_are_not_retried()
    {
        var skipped = Plan("8ch.m4a") with { Action = PlanAction.Unsupported, Reason = "暂不支持 8 声道" };
        var service = new FakeService { Plans = [First, Second, skipped], FailSecond = true };
        var vm = await Create(service);
        await vm.PreviewAsync();
        await vm.ConvertAsync();
        Assert.Equal("已跳过", vm.Items[2].StatusText);
        Assert.Equal("不转换", vm.Items[2].ActionText);
        Assert.Contains("8 声道", vm.Items[2].Detail);
        Assert.Contains("完成 1", vm.ResultsSummary);
        Assert.Contains("失败 1", vm.ResultsSummary);
        Assert.Contains("跳过 1", vm.ResultsSummary);
        Assert.True(vm.CanRetry);
        service.FailSecond = false;
        await vm.RetryFailedAsync();
        Assert.Equal(Second, Assert.Single(service.LastConverted));
        Assert.Contains("完成 2", vm.ResultsSummary);
        Assert.Contains("失败 0", vm.ResultsSummary);
        Assert.Contains("跳过 1", vm.ResultsSummary);
        Assert.Equal("已跳过", vm.Items[2].StatusText);
        Assert.False(vm.CanRetry);
        Assert.True(vm.CanExport);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Batch_exception_or_cancellation_does_not_turn_unsupported_rows_into_failure(bool cancelled)
    {
        var skipped = First with { Action = PlanAction.Unsupported };
        var service = new FakeService { Plans = [skipped, Second], HoldConversion = cancelled,
            ConversionError = cancelled ? null : new IOException("模拟引擎失败") };
        var vm = await Create(service);
        await vm.PreviewAsync();
        var conversion = vm.ConvertAsync();
        if (cancelled)
        {
            await service.Started.Task;
            vm.RequestCancel();
            service.AllowCleanup.TrySetResult();
        }
        await conversion;
        Assert.Equal("已跳过", vm.Items[0].StatusText);
        Assert.Contains("跳过 1", vm.ResultsSummary);
        Assert.Equal(cancelled ? ItemStatus.Cancelled : ItemStatus.Failed, vm.Results[1].Status);
    }

    private static readonly PlannedFile First = Plan("first.flac");
    private static readonly PlannedFile Second = Plan("second.flac");

    private static PlannedFile Plan(string name) => new(new SourceFile(Path.GetFullPath(name), name),
        null, PlanAction.EncodeAlac, "转为 ALAC", ".m4a", 1024);

    private static async Task<WorkspaceViewModel> Create(FakeService service)
    {
        var vm = new WorkspaceViewModel(service);
        vm.OutputDirectory = Path.GetFullPath("converted");
        await vm.AddInputs([First.Source.FullPath, Second.Source.FullPath]);
        return vm;
    }

    [Fact]
    public async Task Conversion_requires_explicit_preview_and_cannot_repeat_completed_plan()
    {
        var service = new FakeService();
        var vm = await Create(service);
        Assert.False(vm.CanConvert);
        await vm.ConvertAsync();
        Assert.Equal(0, service.ConvertCalls);
        await vm.PreviewAsync();
        Assert.True(vm.CanConvert);
        Assert.Equal(0, service.ConvertCalls);
        await vm.ConvertAsync();
        Assert.False(vm.CanConvert);
        Assert.False(vm.IsBusy);
        Assert.Equal(2, vm.Results.Count);
    }

    [Theory]
    [InlineData("output")]
    [InlineData("mode")]
    [InlineData("folders")]
    [InlineData("verify")]
    [InlineData("metadata")]
    [InlineData("input")]
    public async Task Every_plan_input_invalidates_preview(string change)
    {
        var vm = await Create(new FakeService());
        await vm.PreviewAsync();
        Assert.True(vm.CanConvert);
        switch (change)
        {
            case "output": vm.OutputDirectory = Path.GetFullPath("elsewhere"); break;
            case "mode": vm.ModeIndex = 1; break;
            case "folders": vm.PreserveFolders = false; break;
            case "verify": vm.VerifyAudio = false; break;
            case "metadata": vm.PreserveMetadata = false; break;
            case "input": await vm.AddInputs([Path.GetFullPath("third.mp3")]); break;
        }
        Assert.False(vm.CanConvert);
        Assert.False(vm.CanRetry);
        Assert.Equal(WorkspaceState.NeedsPreview, vm.State);
    }

    [Fact]
    public async Task Busy_work_rejects_input_changes_and_shutdown_waits_for_cleanup()
    {
        var service = new FakeService { HoldConversion = true };
        var vm = await Create(service);
        await vm.PreviewAsync();
        var run = vm.ConvertAsync();
        await service.Started.Task;
        Assert.True(vm.IsBusy);
        var output = vm.OutputDirectory;
        vm.OutputDirectory = "ignored";
        vm.ModeIndex = 2;
        await vm.AddInputs(["ignored.mp3"]);
        vm.Clear();
        Assert.Equal(output, vm.OutputDirectory);
        Assert.Equal(0, vm.ModeIndex);
        Assert.Equal(2, vm.InputCount);
        var closing = vm.CancelAndWaitAsync();
        await service.CancellationSeen.Task;
        Assert.False(closing.IsCompleted);
        Assert.True(vm.IsBusy);
        service.AllowCleanup.SetResult();
        await closing;
        await run;
        Assert.True(service.CleanedUp);
        Assert.False(vm.IsBusy);
        Assert.False(vm.CanPreview);
    }

    [Fact]
    public async Task Retry_passes_only_failed_plans_and_keeps_previous_successes()
    {
        var service = new FakeService { FailSecond = true };
        var vm = await Create(service);
        await vm.PreviewAsync();
        await vm.ConvertAsync();
        Assert.True(vm.CanRetry);
        service.FailSecond = false;
        await vm.RetryFailedAsync();
        Assert.Single(service.LastConverted);
        Assert.Equal(Second, service.LastConverted[0]);
        Assert.Equal(2, vm.Results.Count);
        Assert.All(vm.Results, result => Assert.Equal(ItemStatus.Succeeded, result.Status));
        Assert.False(vm.CanRetry);
    }

    [Fact]
    public async Task Changing_options_after_failure_disables_retry_and_stale_report()
    {
        var vm = await Create(new FakeService { FailSecond = true });
        await vm.PreviewAsync();
        await vm.ConvertAsync();
        vm.PreserveMetadata = false;
        Assert.False(vm.CanRetry);
        Assert.False(vm.CanExport);
    }

    [Fact]
    public async Task Unsupported_reason_is_visible_and_cannot_be_converted()
    {
        var unsupported = First with { Action = PlanAction.Unsupported, Reason = "暂不支持 DSD" };
        var vm = await Create(new FakeService { Plans = [unsupported] });
        await vm.PreviewAsync();
        Assert.False(vm.CanConvert);
        Assert.Contains("DSD", vm.Items[0].Detail);
    }

    [Fact]
    public async Task Service_exceptions_are_shown_without_leaving_busy_state()
    {
        var vm = await Create(new FakeService { PreviewError = new IOException("缺少 ffprobe") });
        await vm.PreviewAsync();
        Assert.False(vm.IsBusy);
        Assert.False(vm.CanConvert);
        Assert.Contains("缺少 ffprobe", vm.StatusMessage);
    }

    [Fact]
    public async Task Clear_invalidates_all_results_and_duplicate_inputs_do_not_invalidate_plan()
    {
        var vm = await Create(new FakeService());
        await vm.PreviewAsync();
        await vm.AddInputs([First.Source.FullPath]);
        Assert.True(vm.CanConvert);
        vm.Clear();
        Assert.False(vm.CanConvert);
        Assert.Empty(vm.Items);
        Assert.Empty(vm.Results);
        Assert.Equal(WorkspaceState.Empty, vm.State);
    }

    [Fact]
    public void Safety_defaults_and_warning_are_explicit()
    {
        var vm = new WorkspaceViewModel(new FakeService());
        Assert.True(vm.VerifyAudio);
        Assert.True(vm.PreserveMetadata);
        Assert.True(vm.PreserveFolders);
        Assert.False(vm.ShowVerificationWarning);
        vm.VerifyAudio = false;
        Assert.True(vm.ShowVerificationWarning);
    }

    [Fact]
    public async Task Late_progress_cannot_overwrite_completed_or_invalidated_state()
    {
        var pending = new Queue<Action>();
        var vm = new WorkspaceViewModel(new FakeService(), pending.Enqueue);
        vm.OutputDirectory = Path.GetFullPath("converted");
        await vm.AddInputs([First.Source.FullPath]);
        await vm.PreviewAsync();
        await vm.ConvertAsync();
        Assert.NotEmpty(pending);
        vm.Clear();
        while (pending.TryDequeue(out var callback)) callback();
        Assert.Equal(WorkspaceState.Empty, vm.State);
        Assert.Empty(vm.Items);
        Assert.Empty(vm.Results);
        Assert.Equal(0, vm.BatchPercent);
    }

    [Fact]
    public async Task Report_is_only_available_after_conversion_and_records_returned_path()
    {
        var service = new FakeService();
        var vm = await Create(service);
        await vm.ExportReportAsync("ignored");
        Assert.Equal(0, service.ReportCalls);
        await vm.PreviewAsync();
        Assert.False(vm.CanExport);
        await vm.ConvertAsync();
        var directory = Path.GetFullPath("reports");
        await vm.ExportReportAsync(directory);
        Assert.Equal(1, service.ReportCalls);
        Assert.Equal(Path.Combine(directory, "report.json"), vm.LastReportPath);
        Assert.False(vm.IsBusy);
        Assert.True(vm.CanExport);
    }

    [Fact]
    public async Task Report_error_does_not_lose_results_or_enable_duplicate_conversion()
    {
        var vm = await Create(new FakeService { ReportError = new IOException("目录只读") });
        await vm.PreviewAsync();
        await vm.ConvertAsync();
        await vm.ExportReportAsync(Path.GetFullPath("reports"));
        Assert.Contains("目录只读", vm.StatusMessage);
        Assert.Equal(2, vm.Results.Count);
        Assert.True(vm.CanExport);
        Assert.False(vm.CanConvert);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Conversion_progress_updates_item_and_never_enables_a_second_operation()
    {
        var service = new FakeService { HoldConversion = true };
        var vm = await Create(service);
        await vm.PreviewAsync();
        var run = vm.ConvertAsync();
        await service.Started.Task;
        Assert.True(vm.Items[0].IsProcessing);
        Assert.Equal(25, vm.Items[0].Percent);
        Assert.False(vm.CanPreview);
        Assert.False(vm.CanConvert);
        Assert.False(vm.CanExport);
        vm.RequestCancel();
        service.AllowCleanup.TrySetResult();
        await run;
        Assert.All(vm.Results, result => Assert.Equal(ItemStatus.Cancelled, result.Status));
        Assert.All(vm.Items, item => Assert.False(item.IsProcessing));
    }

    internal sealed class FakeService : IConversionService
    {
        public IReadOnlyList<PlannedFile> Plans { get; init; } = [First, Second];
        public bool HoldConversion { get; init; }
        public bool HoldScan { get; init; }
        public Exception? ScanError { get; init; }
        public bool FailSecond { get; set; }
        public Exception? PreviewError { get; init; }
        public Exception? ReportError { get; init; }
        public Exception? ConversionError { get; init; }
        public int ConvertCalls { get; private set; }
        public int ReportCalls { get; private set; }
        public List<PlannedFile> LastConverted { get; private set; } = [];
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationSeen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowCleanup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CleanedUp { get; private set; }

        public async Task<ScanResult> ScanAsync(IEnumerable<string> inputs, string outputDirectory, CancellationToken cancellationToken)
        {
            if (HoldScan)
            {
                Started.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                catch (OperationCanceledException) { CancellationSeen.TrySetResult(); }
                await AllowCleanup.Task;
                CleanedUp = true;
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (ScanError is not null) throw ScanError;
            return SourceScanner.Scan(inputs, outputDirectory, cancellationToken);
        }

        public Task<PreflightResult> PreviewAsync(IEnumerable<string> inputs, ConversionOptions options,
            IProgress<string>? progress, CancellationToken cancellationToken) =>
            PreviewError is null
                ? Task.FromResult(new PreflightResult(Plans, [], 2048, 100_000_000))
                : Task.FromException<PreflightResult>(PreviewError);

        public async Task<IReadOnlyList<ConversionResult>> ConvertAsync(IEnumerable<PlannedFile> files,
            ConversionOptions options, IProgress<BatchProgress>? progress, CancellationToken cancellationToken)
        {
            ConvertCalls++;
            LastConverted = files.ToList();
            progress?.Report(new BatchProgress(1, LastConverted.Count, LastConverted[0].Source.FullPath,
                .25, "正在处理"));
            Started.TrySetResult();
            if (ConversionError is not null) throw ConversionError;
            if (HoldConversion)
            {
                try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                catch (OperationCanceledException) { CancellationSeen.TrySetResult(); }
                await AllowCleanup.Task;
                CleanedUp = true;
                cancellationToken.ThrowIfCancellationRequested();
            }
            return LastConverted.Select(file => new ConversionResult(file,
                file.Action == PlanAction.Unsupported ? ItemStatus.Skipped :
                FailSecond && file == Second ? ItemStatus.Failed : ItemStatus.Succeeded,
                file.Action == PlanAction.Unsupported ? null : Path.Combine(options.OutputDirectory, file.Source.RelativePath),
                file.Action == PlanAction.Unsupported ? file.Reason : "完成", null, 1)).ToList();
        }

        public Task<string> WriteReportAsync(string directory, IReadOnlyList<ConversionResult> results,
            CancellationToken cancellationToken)
        {
            ReportCalls++;
            return ReportError is null ? Task.FromResult(Path.Combine(directory, "report.json"))
                : Task.FromException<string>(ReportError);
        }
    }
}

