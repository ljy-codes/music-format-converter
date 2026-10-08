namespace MusicFormatConverter.Core;

public static class ErrorMessages
{
    public static string Summary(Exception exception) => exception switch
    {
        OperationCanceledException => "已取消",
        UnauthorizedAccessException => "没有文件或目录访问权限",
        FileNotFoundException => "文件或转换引擎不存在",
        DirectoryNotFoundException => "目录不存在或设备已断开",
        TimeoutException => "处理长时间无响应，已停止当前文件",
        AudioProcessingException error => error.Message,
        IOException io when (io.HResult & 0xffff) is 112 or 39 => "输出磁盘空间不足",
        IOException io => Short(io.Message),
        _ => Short(exception.Message)
    };

    private static string Short(string message) => message.Replace('\r', ' ').Replace('\n', ' ') is var value
        ? value[..Math.Min(value.Length, 180)] : "处理失败";
}

public sealed class AudioProcessingException(string message) : Exception(message);
