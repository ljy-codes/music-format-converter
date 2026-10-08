using System.Diagnostics;
using System.Security.Cryptography;

namespace MusicFormatConverter.Core;

public sealed class MusicConverter(EnginePaths? engines = null)
{
    public async Task<PreflightResult> PreviewAsync(IEnumerable<string> inputs, ConversionOptions options,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ValidateOptions(options);
        var paths = engines ?? EnginePaths.Discover();
        var probe = new AudioProbe(paths);
        var sources = inputs.ToArray();
        progress?.Report("正在扫描文件，仅探测，不写入音频…");
        var scan = await Task.Run(() => SourceScanner.Scan(sources, options.OutputDirectory, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        var plans = new List<PlannedFile>();
        var warnings = scan.Warnings.ToList();
        bool? mp3Available = null;
        foreach (var source in scan.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"正在识别 {plans.Count + 1}/{scan.Files.Count}：{Path.GetFileName(source.FullPath)}");
            try
            {
                var before = new FileInfo(source.FullPath);
                var stamp = before.LastWriteTimeUtc.Ticks;
                var audio = await probe.ReadAsync(source.FullPath, cancellationToken).ConfigureAwait(false);
                if (File.GetLastWriteTimeUtc(source.FullPath).Ticks != stamp)
                    throw new AudioProcessingException("探测期间文件发生变化，请重新预检。");
                var plan = AudioPolicy.Plan(source, audio, options.Mode);
                if (plan.Action == PlanAction.EncodeMp3)
                {
                    mp3Available ??= await EncoderCapabilities.HasMp3EncoderAsync(paths, cancellationToken).ConfigureAwait(false);
                    if (!mp3Available.Value)
                        plan = plan with { Action = PlanAction.Unsupported, OutputExtension = "", EstimatedBytes = 0,
                            Reason = "当前离线引擎未包含 MP3 编码器（libmp3lame）；请选择 Apple Music 智能保真、AAC、FLAC 或 WAV。已有 MP3 仍可原样复制。" };
                }
                plans.Add(plan with { SourceWriteTimeUtcTicks = stamp });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                plans.Add(new(source, null, PlanAction.Unsupported, ErrorMessages.Summary(ex), "", 0));
            }
        }
        var total = (long)Math.Min(plans.Sum(p => (double)p.EstimatedBytes), long.MaxValue);
        var available = OutputFiles.AvailableBytes(options.OutputDirectory);
        if (available.HasValue && total + 16d * 1024 * 1024 > available.Value)
            warnings.Add("预计所需空间超过当前可用空间。估算有余量但不是精确值，请更换输出盘或减少本批文件。");
        if (plans.Any(p => p.Audio is { DurationSeconds: <= 0 } && p.Action != PlanAction.Unsupported))
            warnings.Add("部分文件时长未知，空间和进度估算可能不准确。");
        warnings.Add(options.Mode is ConversionMode.Flac or ConversionMode.Wav or ConversionMode.Mp3
            ? "当前为自选输出格式，不按 Apple Music 智能策略选择格式；用于 Apple Music 时建议默认智能保真。不会操作播放器资料库。"
            : "兼容规则为保守候选，Apple Music Windows/macOS 实际导入仍需对应版本验收；本工具不会操作播放器资料库。");
        return new(plans, warnings, total, available);
    }

    public async Task<IReadOnlyList<ConversionResult>> ConvertAsync(IEnumerable<PlannedFile> files, ConversionOptions options,
        IProgress<BatchProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ValidateOptions(options);
        var plans = files.ToArray();
        var results = new List<ConversionResult>(plans.Length);
        var paths = engines ?? EnginePaths.Discover();
        var probe = new AudioProbe(paths);
        var ffmpeg = new FfmpegAudio(paths);
        string? batchStop = null;
        for (var index = 0; index < plans.Length; index++)
        {
            var plan = plans[index];
            var clock = Stopwatch.StartNew();
            ConversionResult result;
            if (plan.Action == PlanAction.Unsupported)
                result = new(plan, ItemStatus.Skipped, null, plan.Reason, null, 0);
            else if (cancellationToken.IsCancellationRequested || batchStop != null)
                result = new(plan, ItemStatus.Cancelled, null, batchStop ?? "已取消，未生成输出。", null, 0);
            else if (plan.Audio == null)
                result = new(plan, ItemStatus.Failed, null, "转换计划缺少音频信息，请重新预检。", null, 0);
            else
            {
                string? temporary = null;
                string? cleanupWarning = null;
                try
                {
                    void Report(double fraction, string message) =>
                        progress?.Report(new(index + 1, plans.Length, plan.Source.FullPath, fraction, message));
                    Report(0, "检查来源及输出目录");
                    var currentSource = new FileInfo(plan.Source.FullPath);
                    if (!currentSource.Exists) throw new FileNotFoundException("源文件不存在。");
                    if (currentSource.Length != plan.Audio.FileSize ||
                        plan.SourceWriteTimeUtcTicks != 0 && currentSource.LastWriteTimeUtc.Ticks != plan.SourceWriteTimeUtcTicks)
                        throw new AudioProcessingException("预检后源文件发生变化，请重新预检。");
                    if (SourceScanner.IsLink(plan.Source.FullPath)) throw new AudioProcessingException("源文件变为链接，请重新选择。");
                    // On Windows this also prevents writes/deletes while a conversion is using the source.
                    await using var sourceLock = new FileStream(plan.Source.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                        65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    var currentAudio = await probe.ReadAsync(plan.Source.FullPath, cancellationToken).ConfigureAwait(false);
                    var actual = AudioPolicy.Plan(plan.Source, currentAudio, options.Mode);
                    if (actual.Action != plan.Action || currentAudio.CodecName != plan.Audio.CodecName ||
                        currentAudio.SampleRate != plan.Audio.SampleRate || currentAudio.Channels != plan.Audio.Channels ||
                        currentAudio.BitsPerSample != plan.Audio.BitsPerSample)
                        throw new AudioProcessingException("来源或模式与预检计划不一致，请重新预检。");
                    var destination = OutputFiles.Destination(plan.Source, plan.OutputExtension, options);
                    var directory = Path.GetDirectoryName(destination)!;
                    OutputFiles.EnsureSafeDirectory(directory);
                    var available = OutputFiles.AvailableBytes(directory);
                    if (available.HasValue && available.Value < plan.EstimatedBytes + 16d * 1024 * 1024)
                        throw new IOException("输出磁盘空间不足", unchecked((int)0x80070070));
                    var initialStamp = currentSource.LastWriteTimeUtc.Ticks;
                    AudioCheck? sourceCheck = null;
                    if (options.VerifyAudio)
                    {
                        sourceCheck = await ffmpeg.CheckAsync(plan.Source.FullPath, currentAudio.AudioStreamIndex,
                            seconds => Report(currentAudio.DurationSeconds > 0 ? Math.Min(.2, seconds / currentAudio.DurationSeconds * .2) : 0,
                                "检查源音频完整性"), cancellationToken).ConfigureAwait(false);
                        CheckDeclaredLength(currentAudio, sourceCheck);
                    }
                    temporary = Path.Combine(directory, ".mfc-" + Guid.NewGuid().ToString("N") + ".part");
                    string? sourceHash = null;
                    if (plan.Action == PlanAction.Copy)
                    {
                        Report(.25, "原样复制，不重新编码");
                        await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                         65536, FileOptions.Asynchronous))
                            await sourceLock.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                        sourceLock.Position = 0;
                        sourceHash = Convert.ToHexString(await SHA256.HashDataAsync(sourceLock, cancellationToken).ConfigureAwait(false));
                    }
                    else
                    {
                        await ffmpeg.EncodeAsync(plan, options, temporary,
                            value => Report(.2 + value * .6, plan.Reason), cancellationToken).ConfigureAwait(false);
                    }
                    Report(.82, "校验输出音频与标签");
                    var outputAudio = await probe.ReadAsync(temporary, cancellationToken).ConfigureAwait(false);
                    CheckOutputParameters(plan, outputAudio);
                    if (plan.Action != PlanAction.Copy)
                    {
                        // Output verification is mandatory even when the optional source deep check is off.
                        var check = await ffmpeg.CheckAsync(temporary, outputAudio.AudioStreamIndex, null, cancellationToken).ConfigureAwait(false);
                        CheckDeclaredLength(outputAudio, check);
                        if (currentAudio.DeclaredSamples.HasValue && !AudioPolicy.IsLossyEncoding(plan.Action))
                            CheckDeclaredLength(currentAudio, check);
                        if (sourceCheck != null)
                        {
                            if ((AudioPolicy.IsLosslessEncoding(plan.Action) && !AudioPolicy.IsLossy(currentAudio) ||
                                 plan.Action == PlanAction.Remux) && check.Hash != sourceCheck.Hash)
                                throw new AudioProcessingException("输出与源音频的 PCM 校验不一致，未保留结果。");
                            var tolerance = AudioPolicy.IsLossyEncoding(plan.Action) ? .15 : .05;
                            if (Math.Abs(check.DurationSeconds - sourceCheck.DurationSeconds) > tolerance)
                                throw new AudioProcessingException("转换前后解码时长不一致，未保留结果。");
                        }
                    }
                    await using var completed = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read,
                        65536, FileOptions.Asynchronous);
                    var hash = Convert.ToHexString(await SHA256.HashDataAsync(completed, cancellationToken).ConfigureAwait(false));
                    await completed.DisposeAsync().ConfigureAwait(false);
                    if (sourceHash != null && sourceHash != hash)
                        throw new AudioProcessingException("复制内容校验不一致，未保留结果。");
                    currentSource.Refresh();
                    if (currentSource.Length != plan.Audio.FileSize || currentSource.LastWriteTimeUtc.Ticks != initialStamp)
                        throw new AudioProcessingException("转换期间源文件发生变化，未保留结果。");
                    cancellationToken.ThrowIfCancellationRequested();
                    var target = OutputFiles.Commit(temporary, destination);
                    temporary = null;
                    var message = SuccessMessage(plan, outputAudio, options);
                    result = new(plan, ItemStatus.Succeeded, target, message, hash, clock.Elapsed.TotalSeconds);
                }
                catch (OperationCanceledException)
                { result = new(plan, ItemStatus.Cancelled, null, "已取消，未保留临时结果。", null, clock.Elapsed.TotalSeconds); }
                catch (Exception ex)
                {
                    result = new(plan, ItemStatus.Failed, null, ErrorMessages.Summary(ex), null, clock.Elapsed.TotalSeconds);
                    if (ex is IOException io && (io.HResult & 0xffff) is 112 or 39 ||
                        !Directory.Exists(Path.GetPathRoot(Path.GetFullPath(options.OutputDirectory))))
                        batchStop = "输出盘空间不足或不可用，本批其余任务已停止；修复后重新预检。";
                }
                finally
                {
                    if (temporary != null)
                        cleanupWarning = await CleanupTemporaryAsync(temporary).ConfigureAwait(false);
                }
                if (cleanupWarning != null)
                    result = result with
                    {
                        Message = (result.Status == ItemStatus.Cancelled ? "已取消。" : result.Message) + cleanupWarning,
                        ElapsedSeconds = clock.Elapsed.TotalSeconds
                    };
            }
            results.Add(result);
            progress?.Report(new(index + 1, plans.Length, plan.Source.FullPath, 1, result.Message, result));
        }
        return results;
    }

    private static async Task<string?> CleanupTemporaryAsync(string temporary)
    {
        // The engine has been awaited before this point. Antivirus/indexers or
        // other readers can still briefly hold Windows handles without delete sharing.
        // Retry only this exact owned path, never enumerate/delete other .part files.
        // Cleanup must finish even when the conversion token is already cancelled.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Delete(temporary);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 4)
                    return $"临时文件清理失败，未生成正式输出；文件可能仍被占用，请检查：{temporary}";
                await Task.Delay(100 << attempt).ConfigureAwait(false);
            }
        }
    }

    private static void ValidateOptions(ConversionOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.OutputDirectory))
            throw new ArgumentException("请先选择输出目录。");
        _ = Path.GetFullPath(options.OutputDirectory);
        if (!Enum.IsDefined(options.Mode)) throw new ArgumentException("转换模式无效。");
    }

    private static void CheckOutputParameters(PlannedFile plan, AudioInfo output)
    {
        var source = plan.Audio!;
        var codec = plan.Action switch
        {
            PlanAction.EncodeAlac => "alac",
            PlanAction.EncodeAac or PlanAction.Remux => "aac",
            PlanAction.EncodeMp3 => "mp3",
            PlanAction.EncodeFlac => "flac",
            PlanAction.EncodeWav => AudioPolicy.IsLossy(source) || source.BitsPerSample > 16 ? "pcm_s24le" : "pcm_s16le",
            _ => source.CodecName
        };
        if (output.CodecName != codec || output.SampleRate != source.SampleRate || output.Channels != source.Channels ||
            output.FileSize <= 0)
            throw new AudioProcessingException("输出编码、采样率或声道与计划不一致。");
        if (AudioPolicy.IsLosslessEncoding(plan.Action) && !AudioPolicy.IsLossy(source) && output.BitsPerSample < source.BitsPerSample)
            throw new AudioProcessingException("输出有效位深降低，未保留结果。");
    }

    private static void CheckDeclaredLength(AudioInfo audio, AudioCheck check)
    {
        if (audio.DeclaredSamples is { } samples &&
            Math.Abs(check.DurationSeconds * audio.SampleRate - samples) > 2)
            throw new AudioProcessingException("音频不完整：实际解码样本数与文件声明不一致。");
    }

    private static string SuccessMessage(PlannedFile plan, AudioInfo output, ConversionOptions options)
    {
        var warnings = new List<string>();
        if (!options.VerifyAudio)
            warnings.Add(plan.Action == PlanAction.Copy ? "未做完整音频检查" : "未对源音频做独立完整检查");
        if (plan.Action != PlanAction.Copy && options.PreserveMetadata)
        {
            foreach (var key in new[] { "title", "artist", "album", "album_artist", "track", "disc", "date", "genre" })
                if (plan.Audio!.Tags.TryGetValue(key, out var expected) && !string.IsNullOrWhiteSpace(expected) &&
                    (!output.Tags.TryGetValue(key, out var actual) || expected != actual))
                    warnings.Add($"标签 {key} 未完整保留");
            if (plan.Audio!.CoverStreamIndex != null && output.CoverStreamIndex == null)
                warnings.Add("封面未保留");
            if (plan.Audio.CoverCount > 1) warnings.Add("仅保留第一张内嵌封面");
        }
        if (AudioPolicy.IsLosslessEncoding(plan.Action) && AudioPolicy.IsLossy(plan.Audio!))
            warnings.Add($"有损来源转 {output.CodecName.ToUpperInvariant()} 不提升原音质");
        return warnings.Count == 0 ? "完成，校验通过。" : "完成；" + string.Join("；", warnings);
    }
}
