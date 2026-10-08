using MusicFormatConverter.Core;

namespace MusicFormatConverter.App.ViewModels;

public sealed class QueueItemViewModel : ObservableObject
{
    public QueueItemViewModel(string path)
    {
        SourcePath = path;
        Detail = "等待预检 · 尚未读取音频信息";
    }

    public QueueItemViewModel(PlannedFile plan) : this(plan.Source.FullPath)
    {
        Plan = plan;
        Detail = plan.Reason;
        StatusText = plan.Action == PlanAction.Unsupported ? "不支持" : "已就绪";
    }

    public PlannedFile? Plan { get; }
    public string SourcePath { get; }
    public string Name => Path.GetFileName(SourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    public string Detail { get; private set; }
    public string StatusText { get; private set; } = "待预检";
    public double Percent { get; private set; }
    public bool IsProcessing { get; private set; }
    public string FormatText => Plan?.Audio is { } audio
        ? $"{audio.CodecName.ToUpperInvariant()} · {audio.SampleRate / 1000d:0.#} kHz · {audio.Channels} 声道"
        : Plan is null ? $"候选文件 · {Path.GetExtension(SourcePath).TrimStart('.').ToUpperInvariant()}（待识别）" : "音频信息不可用";
    public string ActionText => Plan?.Action switch
    {
        PlanAction.Copy => "原样复制",
        PlanAction.Remux => "无损封装",
        PlanAction.EncodeAlac => "ALAC",
        PlanAction.EncodeAac => "AAC",
        PlanAction.EncodeMp3 => "MP3",
        PlanAction.EncodeFlac => "FLAC",
        PlanAction.EncodeWav => "WAV",
        PlanAction.Unsupported => "不转换",
        _ => "—"
    };
    public string FullDetail => $"{SourcePath}\n{FormatText}\n{ActionText} · {StatusText}\n{Detail}";

    internal void UpdateProgress(double fraction, string message)
    {
        Percent = Math.Clamp(double.IsFinite(fraction) ? fraction * 100 : 0, 0, 100);
        IsProcessing = true;
        StatusText = "处理中";
        Detail = message;
        NotifyAll();
    }

    internal void Apply(ConversionResult result)
    {
        IsProcessing = false;
        Percent = result.Status == ItemStatus.Succeeded ? 100 : Percent;
        StatusText = result.Status switch
        {
            ItemStatus.Succeeded => "已完成",
            ItemStatus.Failed => "失败",
            ItemStatus.Skipped => "已跳过",
            ItemStatus.Cancelled => "已取消",
            ItemStatus.Processing => "处理中",
            _ => "已就绪"
        };
        Detail = result.Message + (result.OutputPath is { Length: > 0 } path ? $" · {path}" : "");
        NotifyAll();
    }
}
