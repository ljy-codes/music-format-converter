using System.Text;

namespace MusicFormatConverter.Core;

public static class OutputFiles
{
    public static string SafeSegment(string value)
    {
        var safe = new string(value.Normalize(NormalizationForm.FormC)
            .Select(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c) ? '_' : c).ToArray()).TrimEnd(' ', '.');
        if (string.IsNullOrEmpty(safe)) safe = "_";
        var stem = safe.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" ||
            stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3]))
            safe = "_" + safe;
        // Keep room for an extension, collision suffix, and UTF-8 filesystem component limit.
        while (Encoding.UTF8.GetByteCount(safe) > 180)
            safe = safe[..(char.IsLowSurrogate(safe[^1]) && safe.Length > 1 ? ^2 : ^1)];
        return safe;
    }

    public static string Destination(SourceFile source, string extension, ConversionOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.OutputDirectory)) throw new IOException("请选择输出目录。");
        var root = Path.GetFullPath(options.OutputDirectory);
        var relative = source.RelativePath.Replace('\\', '/');
        var parts = relative.Split('/');
        if (Path.IsPathRooted(relative) || parts.Any(p => p is ".." or "." or "") ||
            !extension.StartsWith('.') || extension.Any(c => c is '/' or '\\'))
            throw new IOException("输出相对路径无效。");
        var name = SafeSegment(Path.GetFileNameWithoutExtension(parts[^1])) + extension;
        var subdirectories = options.PreserveFolders ? parts[..^1].Select(SafeSegment) : [];
        var destination = Path.Combine([root, .. subdirectories, name]);
        if (!SourceScanner.IsWithin(destination, root)) throw new IOException("输出路径超出指定目录。");
        return destination;
    }

    public static void EnsureSafeDirectory(string directory)
    {
        var full = Path.GetFullPath(directory);
        CheckAncestors(full);
        Directory.CreateDirectory(full);
        CheckAncestors(full);
    }

    private static void CheckAncestors(string path)
    {
        for (DirectoryInfo? dir = new(path); dir != null; dir = dir.Parent)
            if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("输出路径包含目录链接；请选择普通目录。");
    }

    public static string Commit(string temp, string destination)
    {
        EnsureSafeDirectory(Path.GetDirectoryName(destination)!);
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(temp)),
                Path.GetDirectoryName(Path.GetFullPath(destination)), SourceScanner.PathComparison))
            throw new IOException("临时文件必须位于最终输出目录。");
        var dir = Path.GetDirectoryName(destination)!;
        var name = Path.GetFileNameWithoutExtension(destination);
        var extension = Path.GetExtension(destination);
        for (var i = 0; i < 10000; i++)
        {
            var candidate = i == 0 ? destination : Path.Combine(dir, $"{name} ({i + 1}){extension}");
            try
            {
                File.Move(temp, candidate, overwrite: false);
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate) || Directory.Exists(candidate)) { }
        }
        throw new IOException("同名文件过多，无法生成安全输出名称。");
    }

    public static long? AvailableBytes(string directory)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))!).AvailableFreeSpace; }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return null; }
    }
}
