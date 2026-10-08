using System.Diagnostics;
using System.Text;

namespace MusicFormatConverter.Core;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string executable, IEnumerable<string> arguments, TimeSpan timeout,
        CancellationToken cancellationToken = default, Action<string>? outputLine = null,
        TimeSpan? idleTimeout = null)
    {
        using var deadline = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        using var process = new Process
        {
            StartInfo = new()
            {
                FileName = executable, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                RedirectStandardInput = true, StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        cancellationToken.ThrowIfCancellationRequested();
        if (!process.Start()) throw new IOException("无法启动转换引擎。");
        process.StandardInput.Close();
        var lastActivity = Stopwatch.GetTimestamp();
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var timedOut = false;
        using var guard = linked.Token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        });
        using var monitorStop = new CancellationTokenSource();
        var monitor = MonitorAsync();
        var readOut = ReadAsync(process.StandardOutput, stdout, outputLine, 2 * 1024 * 1024);
        var readErr = ReadAsync(process.StandardError, stderr, null, 64 * 1024);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(linked.Token), readOut, readErr).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (deadline.IsCancellationRequested || timedOut) throw new TimeoutException();
            return new(process.ExitCode, stdout.ToString(), stderr.ToString());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("转换引擎超时，已停止当前文件。"); }
        finally
        {
            monitorStop.Cancel();
            try { await monitor.ConfigureAwait(false); } catch (OperationCanceledException) { }
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }

        async Task MonitorAsync()
        {
            if (idleTimeout == null) return;
            while (!monitorStop.IsCancellationRequested)
            {
                await Task.Delay(1000, monitorStop.Token).ConfigureAwait(false);
                if (Stopwatch.GetElapsedTime(Interlocked.Read(ref lastActivity)) > idleTimeout.Value)
                {
                    timedOut = true;
                    linked.Cancel();
                    return;
                }
            }
        }

        async Task ReadAsync(StreamReader reader, StringBuilder buffer, Action<string>? callback, int limit)
        {
            var chunk = new char[4096];
            var line = new StringBuilder();
            try
            {
                int count;
                while ((count = await reader.ReadAsync(chunk.AsMemory(), linked.Token).ConfigureAwait(false)) > 0)
                {
                    // Only stdout progress resets the conversion stall timer; repeated errors do not.
                    if (callback != null) Interlocked.Exchange(ref lastActivity, Stopwatch.GetTimestamp());
                    if (buffer.Length + count <= limit) buffer.Append(chunk, 0, count);
                    else if (callback == null && ReferenceEquals(buffer, stdout))
                        throw new AudioProcessingException("探测输出过大，已停止该文件。");
                    // stderr is deliberately bounded, but always drained to prevent pipe deadlocks.
                    if (callback == null) continue;
                    for (var i = 0; i < count; i++)
                    {
                        if (chunk[i] == '\n')
                        {
                            callback(line.ToString().TrimEnd('\r'));
                            line.Clear();
                        }
                        else
                        {
                            if (line.Length > 16384) throw new AudioProcessingException("引擎输出异常。");
                            line.Append(chunk[i]);
                        }
                    }
                }
                if (callback != null && line.Length > 0) callback(line.ToString());
            }
            catch
            {
                linked.Cancel();
                throw;
            }
        }
    }
}
