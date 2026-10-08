using System.Runtime.InteropServices;

namespace MusicFormatConverter.Core;

public sealed record EnginePaths(string Ffmpeg, string Ffprobe)
{
    public static EnginePaths Discover()
    {
        var configured = Environment.GetEnvironmentVariable("MFC_ENGINE_DIR");
        if (!string.IsNullOrWhiteSpace(configured)) return FromDirectory(configured);
        var rid = RuntimeInformation.RuntimeIdentifier;
        var candidates = new List<string> { Path.Combine(AppContext.BaseDirectory, "tools") };
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            candidates.Add(Path.Combine(directory.FullName, "tools", rid));
        foreach (var candidate in candidates.Distinct())
        {
            if (!Directory.Exists(candidate)) continue;
            try { return FromDirectory(candidate); }
            catch (FileNotFoundException) { }
        }
        throw new FileNotFoundException("缺少离线转换引擎。请使用完整安装包，开发版请先运行引擎准备脚本。");
    }

    public static EnginePaths FromDirectory(string directory)
    {
        var extension = OperatingSystem.IsWindows() ? ".exe" : "";
        var ffmpeg = Path.GetFullPath(Path.Combine(directory, "ffmpeg" + extension));
        var ffprobe = Path.GetFullPath(Path.Combine(directory, "ffprobe" + extension));
        if (!File.Exists(ffmpeg) || !File.Exists(ffprobe))
            throw new FileNotFoundException("转换引擎不完整，需要 ffmpeg 和 ffprobe。");
        return new(ffmpeg, ffprobe);
    }
}
