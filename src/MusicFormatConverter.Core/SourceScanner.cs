namespace MusicFormatConverter.Core;

public static class SourceScanner
{
    private static readonly HashSet<string> Attachments = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".txt", ".log", ".json",
        ".csv", ".md", ".pdf", ".ini", ".db", ".lrc", ".cue", ".nfo", ".url",
        ".m3u", ".m3u8", ".pls", ".sfv", ".sha256", ".zip", ".rar", ".7z"
    };
    // macOS can host case-sensitive volumes. Never silently merge distinct source names there.
    internal static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    internal static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static ScanResult Scan(IEnumerable<string> inputs, string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        var files = new List<SourceFile>();
        var warnings = new List<string>();
        var seen = new HashSet<string>(PathComparer);
        var output = string.IsNullOrWhiteSpace(outputDirectory) ? null : Path.GetFullPath(outputDirectory);
        foreach (var input in inputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var path = Path.GetFullPath(input);
                if (output != null && IsWithin(path, output))
                {
                    warnings.Add($"已排除位于输出目录中的来源：{path}");
                    continue;
                }
                if (!File.Exists(path) && !Directory.Exists(path))
                {
                    // Keep missing explicitly selected files: they must get a per-file failure.
                    if (seen.Add(path)) files.Add(new(path, Path.GetFileName(path)));
                    continue;
                }
                if (IsLink(path))
                {
                    warnings.Add($"已跳过链接，避免目录循环或越界：{path}");
                    continue;
                }
                if (File.Exists(path))
                {
                    Add(path, Path.GetFileName(path));
                    continue;
                }
                var rootName = new DirectoryInfo(path).Name;
                if (string.IsNullOrWhiteSpace(rootName)) rootName = "音乐";
                var pending = new Stack<string>();
                pending.Push(path);
                while (pending.TryPop(out var directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (output != null && IsWithin(entry, output)) continue;
                            if (IsLink(entry)) { warnings.Add($"已跳过链接：{entry}"); continue; }
                            if (Directory.Exists(entry)) { pending.Push(entry); continue; }
                            var name = Path.GetFileName(entry);
                            if (name.StartsWith("._", StringComparison.Ordinal) || name.StartsWith(".mfc-", StringComparison.Ordinal) ||
                                name is ".DS_Store" or "Thumbs.db" || Attachments.Contains(Path.GetExtension(entry)))
                            {
                                // CUE needs an explicit visible warning, not silent omission.
                                if (Path.GetExtension(entry).Equals(".cue", StringComparison.OrdinalIgnoreCase))
                                    warnings.Add($"发现 CUE：首版不分轨，整轨音频独立处理。{entry}");
                                continue;
                            }
                            Add(entry, Path.Combine(rootName, Path.GetRelativePath(path, entry)));
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { warnings.Add($"无法扫描目录：{directory}（{ErrorMessages.Summary(ex)}）"); }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { warnings.Add($"无法添加来源：{input}（{ErrorMessages.Summary(ex)}）"); }
        }
        return new(files, warnings);

        void Add(string full, string relative)
        {
            if (seen.Add(full)) files.Add(new(full, relative));
        }
    }

    internal static bool IsWithin(string path, string directory)
    {
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var child = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var prefix = Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar;
        return child.Equals(parent, PathComparison) ||
               child.StartsWith(prefix, PathComparison);
    }

    internal static bool IsLink(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}
