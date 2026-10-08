using System.Collections.ObjectModel;
using MusicFormatConverter.App.Services;
using MusicFormatConverter.Core;

namespace MusicFormatConverter.App.ViewModels;

public enum WorkspaceState { Empty, NeedsPreview, Previewing, Ready, Converting, Cancelling, Completed, Exporting, Scanning }

/// <summary>
/// UI-thread owned state. Progress is marshalled through the injected dispatcher.
/// Operations capture immutable inputs/options; generation fences reject stale progress.
/// </summary>
public sealed class WorkspaceViewModel : ObservableObject
{
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private readonly IConversionService _service;
    private readonly Action<Action> _dispatch;
    private readonly List<string> _inputs = [];
    private readonly ObservableCollection<QueueItemViewModel> _items = [];
    private readonly Dictionary<string, QueueItemViewModel> _rows = new(PathComparer);
    private readonly Dictionary<string, ConversionResult> _results = new(PathComparer);
    private IReadOnlyList<PlannedFile> _plan = [];
    private IReadOnlyList<SourceFile> _listedSources = [];
    private string _scanWarnings = "";
    private bool _listingComplete = true;
    private CancellationTokenSource? _cancellation;
    private Task _activeOperation = Task.CompletedTask;
    private bool _planValid;
    private bool _shuttingDown;
    private long _generation;
    private string _outputDirectory = "";
    private int _modeIndex;
    private bool _preserveFolders = true;
    private bool _verifyAudio = true;
    private bool _preserveMetadata = true;
    private QueueItemViewModel? _selectedItem;

    public WorkspaceViewModel(IConversionService service, Action<Action>? dispatch = null)
    {
        _service = service;
        _dispatch = dispatch ?? (action => action());
        Items = new ReadOnlyObservableCollection<QueueItemViewModel>(_items);
    }

    public ReadOnlyObservableCollection<QueueItemViewModel> Items { get; }
    public IReadOnlyList<ConversionResult> Results => _results.Values.ToArray();
    public WorkspaceState State { get; private set; } = WorkspaceState.Empty;
    public bool IsBusy => _cancellation is not null;
    public bool CanEdit => !IsBusy && !_shuttingDown;
    public bool IsEmpty => _items.Count == 0;
    public bool HasItems => !IsEmpty;
    public int InputCount => _inputs.Count;
    public bool CanPreview => CanEdit && InputCount > 0 && IsOutputValid;
    public bool CanConvert => CanEdit && _planValid && _plan.Any(p => p.Action != PlanAction.Unsupported);
    public bool CanRetry => CanEdit && _results.Values.Any(IsRetryable);
    public bool CanClear => CanEdit && InputCount > 0;
    public bool CanCancel => IsBusy && State != WorkspaceState.Cancelling;
    public bool CanExport => CanEdit && _results.Count > 0;
    public bool CanOpenOutput => CanEdit && IsOutputValid;
    public bool IsIndeterminate => State is WorkspaceState.Scanning or WorkspaceState.Previewing or WorkspaceState.Exporting;
    public bool ShowVerificationWarning => !VerifyAudio;
    public double BatchPercent { get; private set; }
    public string StatusMessage { get; private set; } = "添加音乐文件或文件夹，然后选择输出位置。";
    public string PreviewSummary { get; private set; } = "先预检，再转换。预检不会生成音频文件。";
    public string Warnings { get; private set; } = "";
    public bool HasWarnings => !string.IsNullOrWhiteSpace(Warnings);
    public string LastReportPath { get; private set; } = "";
    public string QueueCaption => State == WorkspaceState.Scanning ? "正在展开文件夹…" :
        InputCount == 0 ? "等待添加音乐" : $"{_items.Count} 个文件 · {InputCount} 个导入来源";
    public string ResultsSummary => _results.Count == 0 ? "原文件始终保持不变"
        : $"完成 {_results.Values.Count(r => r.Status == ItemStatus.Succeeded)}  ·  " +
          $"失败 {_results.Values.Count(r => r.Status == ItemStatus.Failed)}  ·  " +
          $"跳过 {_results.Values.Count(r => r.Status == ItemStatus.Skipped)}  ·  " +
          $"取消 {_results.Values.Count(r => r.Status == ItemStatus.Cancelled)}";
    public string PhaseText => State switch
    {
        WorkspaceState.Scanning => "正在导入",
        WorkspaceState.Previewing => "正在预检",
        WorkspaceState.Ready => "预检完成",
        WorkspaceState.Converting => "正在转换",
        WorkspaceState.Cancelling => "正在安全停止",
        WorkspaceState.Completed => "批次已结束",
        WorkspaceState.Exporting => "正在导出报告",
        WorkspaceState.NeedsPreview => "需要预检",
        _ => "准备就绪"
    };
    public string ProgressCaption => $"{BatchPercent:0}%";
    public string ModeDescription => ModeIndex switch
    {
        1 => "所有受支持的音频转为 ALAC。有损来源不会因此恢复音质。",
        2 => "AAC 256 kbps 有损编码；已有 AAC 优先复制/重封装，不为统一码率重复编码。",
        3 => "自选格式：MP3 320 kbps，有损编码；已有 MP3 原样复制。编码仅支持 32/44.1/48 kHz，不自动重采样。",
        4 => "自选格式：FLAC 无损归档，保留支持的整数精度。有损来源不会恢复音质；Apple Music 用途建议默认模式。",
        5 => "自选格式：WAV PCM，体积较大。不嵌封面，部分标签可能无法保留；有损来源不会恢复音质。",
        _ => "默认面向 Apple Music：兼容文件原样复制，AAC 优先重新封装，其余受支持音频转为 ALAC。"
    };

    public QueueItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set { _selectedItem = value; Notify(); Notify(nameof(HasSelection)); }
    }
    public bool HasSelection => SelectedItem is not null;

    public string OutputDirectory
    {
        get => _outputDirectory;
        set
        {
            value = value?.Trim() ?? "";
            if (!CanEdit || value == _outputDirectory) return;
            _outputDirectory = value;
            Invalidate();
        }
    }
    public int ModeIndex
    {
        get => _modeIndex;
        set
        {
            if (!CanEdit || value is < 0 or > 5 || value == _modeIndex) return;
            _modeIndex = value;
            Invalidate();
        }
    }
    public bool PreserveFolders
    {
        get => _preserveFolders;
        set { if (!CanEdit || value == _preserveFolders) return; _preserveFolders = value; Invalidate(); }
    }
    public bool VerifyAudio
    {
        get => _verifyAudio;
        set { if (!CanEdit || value == _verifyAudio) return; _verifyAudio = value; Invalidate(); }
    }
    public bool PreserveMetadata
    {
        get => _preserveMetadata;
        set { if (!CanEdit || value == _preserveMetadata) return; _preserveMetadata = value; Invalidate(); }
    }
    private bool IsOutputValid => !string.IsNullOrWhiteSpace(OutputDirectory) && Path.IsPathFullyQualified(OutputDirectory);
    private ConversionOptions SnapshotOptions() => new(OutputDirectory, (ConversionMode)ModeIndex,
        PreserveFolders, VerifyAudio, PreserveMetadata);
    private static bool IsRetryable(ConversionResult result) =>
        result.Status == ItemStatus.Failed && result.Plan.Action != PlanAction.Unsupported;

    public async Task AddInputs(IEnumerable<string> paths)
    {
        if (!CanEdit) return;
        var before = _inputs.Count;
        var existing = new HashSet<string>(_inputs, PathComparer);
        var invalid = 0;
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            try
            {
                var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
                if (existing.Add(fullPath)) _inputs.Add(fullPath);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                invalid++;
            }
        }
        if (_inputs.Count != before || !_listingComplete)
        {
            _listingComplete = false;
            Invalidate();
            var snapshot = _inputs.ToArray();
            var output = IsOutputValid ? OutputDirectory : "";
            await RunAsync(WorkspaceState.Scanning, async (token, _) =>
            {
                var scan = await _service.ScanAsync(snapshot, output, token);
                token.ThrowIfCancellationRequested();
                _listedSources = scan.Files;
                _listingComplete = true;
                _scanWarnings = string.Join("\n", scan.Warnings);
                SetPendingRows();
                Warnings = _scanWarnings;
                State = WorkspaceState.NeedsPreview;
                PreviewSummary = $"已列出 {_listedSources.Count} 个候选文件；尚未预检或转换。";
                StatusMessage = _listedSources.Count == 0 ? "文件夹中没有候选音频；请检查扫描提示或添加其他来源。"
                    : !IsOutputValid ? "歌曲已列出。请选择输出文件夹，再点击「开始预检」。"
                    : "歌曲已列出。点击「开始预检」确认格式与转换方案，不会自动转换。";
            });
        }
        if (invalid > 0) ShowError($"有 {invalid} 个路径无法读取，请使用本地文件或文件夹。");
    }

    public void Clear()
    {
        if (!CanEdit) return;
        _inputs.Clear();
        _listedSources = [];
        _scanWarnings = "";
        _listingComplete = true;
        Invalidate();
    }

    private void SetPendingRows()
    {
        _items.Clear();
        SelectedItem = null;
        foreach (var source in _listedSources) _items.Add(new QueueItemViewModel(source.FullPath));
    }

    private void Invalidate()
    {
        _generation++;
        _planValid = false;
        _plan = [];
        _results.Clear();
        _rows.Clear();
        SetPendingRows();
        State = InputCount == 0 ? WorkspaceState.Empty : WorkspaceState.NeedsPreview;
        BatchPercent = 0;
        Warnings = _scanWarnings;
        LastReportPath = "";
        PreviewSummary = "设置或来源发生变化后，必须重新预检。";
        StatusMessage = InputCount == 0 ? "添加音乐文件或文件夹，然后选择输出位置。"
            : !IsOutputValid ? "请选择完整的输出文件夹路径，然后开始预检。"
            : "等待预检：检查格式、转换策略和输出空间。";
        NotifyAll();
    }

    public Task PreviewAsync()
    {
        if (!CanPreview) return Task.CompletedTask;
        var inputs = _inputs.ToArray();
        var options = SnapshotOptions();
        _planValid = false;
        _results.Clear();
        LastReportPath = "";
        return RunAsync(WorkspaceState.Previewing, async (token, generation) =>
        {
            var preview = await _service.PreviewAsync(inputs, options,
                Progress<string>(generation, message => { StatusMessage = message; Notify(nameof(StatusMessage)); }), token);
            token.ThrowIfCancellationRequested();
            _plan = preview.Files.ToArray();
            _listedSources = _plan.Select(p => p.Source).ToArray();
            _listingComplete = true;
            _items.Clear();
            _rows.Clear();
            SelectedItem = null;
            foreach (var file in _plan)
            {
                var item = new QueueItemViewModel(file);
                _items.Add(item);
                _rows[file.Source.FullPath] = item;
            }
            var unsupported = _plan.Count(p => p.Action == PlanAction.Unsupported);
            PreviewSummary = $"{_plan.Count} 项 · 可处理 {_plan.Count - unsupported} · 不支持 {unsupported}\n" +
                $"预计输出 {FormatBytes(preview.EstimatedBytes)}" +
                (preview.AvailableBytes is { } available ? $" · 可用 {FormatBytes(available)}" : " · 可用空间未知");
            Warnings = string.Join("\n", preview.Warnings);
            if (preview.AvailableBytes is { } free && preview.EstimatedBytes > free)
                Warnings = (Warnings + "\n预计空间不足，请更换输出目录或释放空间。").Trim();
            _planValid = true;
            State = WorkspaceState.Ready;
            StatusMessage = _plan.Count == 0 ? "未发现可识别的音频文件，请检查导入来源。"
                : _plan.Count == unsupported ? "没有可转换的文件，请查看队列中的不支持原因。"
                : "预检完成。确认队列和设置后，点击「开始转换」。";
        });
    }

    public Task ConvertAsync() => CanConvert ? ConvertPlansAsync(_plan, retry: false) : Task.CompletedTask;

    public Task RetryFailedAsync() => CanRetry
        ? ConvertPlansAsync(_results.Values.Where(IsRetryable).Select(r => r.Plan).ToArray(), retry: true)
        : Task.CompletedTask;

    private Task ConvertPlansAsync(IReadOnlyList<PlannedFile> plans, bool retry)
    {
        var snapshot = plans.ToArray();
        var options = SnapshotOptions();
        _planValid = false; // A consumed plan cannot trigger duplicate full conversion.
        if (!retry) _results.Clear();
        else foreach (var file in snapshot) _results.Remove(file.Source.FullPath);
        LastReportPath = "";
        BatchPercent = 0;
        return RunAsync(WorkspaceState.Converting, async (token, generation) =>
        {
            try
            {
                var results = await _service.ConvertAsync(snapshot, options,
                    Progress<BatchProgress>(generation, ApplyProgress), token);
                foreach (var result in results) ApplyResult(result);
                CompleteMissing(snapshot, ItemStatus.Cancelled, "未完成；请重新预检后处理。");
                BatchPercent = token.IsCancellationRequested ? BatchPercent : 100;
                StatusMessage = token.IsCancellationRequested ? "转换已取消，已完成文件保留。"
                    : "批次已结束。可以打开输出文件夹、导出报告，或仅重试失败项。";
                State = WorkspaceState.Completed;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                CompleteMissing(snapshot, ItemStatus.Cancelled, "已取消，原文件未修改。");
                throw;
            }
            catch (Exception ex)
            {
                CompleteMissing(snapshot, ItemStatus.Failed, Concise(ex));
                throw;
            }
        });
    }

    private void CompleteMissing(IEnumerable<PlannedFile> plans, ItemStatus status, string message)
    {
        foreach (var file in plans)
            if (!_results.ContainsKey(file.Source.FullPath))
                ApplyResult(file.Action == PlanAction.Unsupported
                    ? new ConversionResult(file, ItemStatus.Skipped, null, file.Reason, null, 0)
                    : new ConversionResult(file, status, null, message, null, 0));
    }

    private void ApplyProgress(BatchProgress progress)
    {
        if (_rows.TryGetValue(progress.SourcePath, out var item))
        {
            if (progress.Result is { } result) ApplyResult(result);
            else item.UpdateProgress(progress.Fraction, progress.Message);
        }
        var fraction = double.IsFinite(progress.Fraction) ? Math.Clamp(progress.Fraction, 0, 1) : 0;
        BatchPercent = progress.Total <= 0 ? 0 :
            Math.Clamp((Math.Max(0, progress.Index - 1) + fraction) / progress.Total * 100, 0, 100);
        StatusMessage = progress.Message;
        Notify(nameof(BatchPercent));
        Notify(nameof(ProgressCaption));
        Notify(nameof(StatusMessage));
        Notify(nameof(ResultsSummary));
    }

    private void ApplyResult(ConversionResult result)
    {
        _results[result.Plan.Source.FullPath] = result;
        if (_rows.TryGetValue(result.Plan.Source.FullPath, out var item)) item.Apply(result);
    }

    public Task ExportReportAsync(string directory)
    {
        if (!CanExport || string.IsNullOrWhiteSpace(directory)) return Task.CompletedTask;
        var results = Results;
        return RunAsync(WorkspaceState.Exporting, async (token, _) =>
        {
            LastReportPath = await _service.WriteReportAsync(directory, results, token);
            StatusMessage = $"报告已导出：{LastReportPath}";
            State = WorkspaceState.Completed;
        });
    }

    public void RequestCancel()
    {
        if (_cancellation is null || _cancellation.IsCancellationRequested) return;
        State = WorkspaceState.Cancelling;
        StatusMessage = "正在停止任务并等待引擎清理，请勿强制结束程序。";
        _cancellation.Cancel();
        NotifyAll();
    }

    public async Task CancelAndWaitAsync()
    {
        _shuttingDown = true;
        RequestCancel();
        NotifyAll();
        await _activeOperation;
    }

    public void ShowError(string message)
    {
        StatusMessage = message;
        Notify(nameof(StatusMessage));
    }

    private Task RunAsync(WorkspaceState state, Func<CancellationToken, long, Task> work)
    {
        if (!CanEdit) return Task.CompletedTask;
        var cancellation = new CancellationTokenSource();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _activeOperation = completion.Task;
        _cancellation = cancellation;
        var generation = ++_generation;
        State = state;
        StatusMessage = state == WorkspaceState.Scanning ? "正在展开文件夹，仅列出文件，不读取音频或写入输出…" :
            state == WorkspaceState.Previewing ? "正在扫描并探测音频…" :
            state == WorkspaceState.Exporting ? "正在导出报告…" : "正在处理队列…";
        NotifyAll();
        _ = ExecuteAsync();
        return completion.Task;

        async Task ExecuteAsync()
        {
            try { await work(cancellation.Token, generation); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                State = state is WorkspaceState.Previewing or WorkspaceState.Scanning ? WorkspaceState.NeedsPreview : WorkspaceState.Completed;
                StatusMessage = state == WorkspaceState.Scanning
                    ? "导入扫描已取消，清单可能不完整；可重新添加来源或通过预检重新扫描。原文件未修改。"
                    : "任务已取消，原文件未修改；引擎已停止。";
            }
            catch (Exception ex)
            {
                State = state is WorkspaceState.Previewing or WorkspaceState.Scanning ? WorkspaceState.NeedsPreview : WorkspaceState.Completed;
                StatusMessage = $"操作未完成：{Concise(ex)}";
            }
            finally
            {
                _cancellation = null;
                cancellation.Dispose();
                NotifyAll();
                completion.TrySetResult();
            }
        }
    }

    private IProgress<T> Progress<T>(long generation, Action<T> handler) =>
        new CallbackProgress<T>(value => _dispatch(() =>
        {
            if (generation == _generation && IsBusy) handler(value);
        }));

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    private static string Concise(Exception error)
    {
        var text = error.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length > 240 ? text[..240] + "…" : text;
    }

    private static string FormatBytes(long bytes) => bytes >= 1L << 30
        ? $"{bytes / (double)(1L << 30):0.##} GB"
        : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0.##} MB" : $"{Math.Max(0, bytes) / 1024d:0.#} KB";
}
