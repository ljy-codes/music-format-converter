namespace MusicFormatConverter.Core.Tests;

public sealed class TestFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(TemporaryRoot(), "mfc-test-" + Guid.NewGuid().ToString("N"));
    private static string TemporaryRoot()
    {
        // macOS TMPDIR normally traverses /var -> /private/var. Use its real
        // location for fixtures without relaxing the application's link rejection.
        var temp = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
        var root = System.IO.Path.GetPathRoot(temp)!;
        foreach (var segment in temp[root.Length..].Split(System.IO.Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = new DirectoryInfo(System.IO.Path.Combine(root, segment));
            root = directory.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? directory.FullName;
        }
        return root;
    }
    public TestFolder() => Directory.CreateDirectory(Path);
    public string File(string name, string contents = "sample")
    {
        var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(Path, name));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, contents);
        return path;
    }
    public void Dispose() => Directory.Delete(Path, true);
}

public class FileSafetyTests
{
    [Fact]
    public void Unix_source_paths_are_not_merged_just_because_only_case_differs()
    {
        using var folder = new TestFolder();
        // Explicit missing selections exercise path identity without assuming this test volume's case mode.
        var first = System.IO.Path.Combine(folder.Path, "Song.flac");
        var second = System.IO.Path.Combine(folder.Path, "song.flac");
        var result = SourceScanner.Scan([first, second], "");
        Assert.Equal(OperatingSystem.IsWindows() ? 1 : 2, result.Files.Count);
    }

    [Fact]
    public void Scan_deduplicates_and_excludes_nested_output_without_deleting_attachments()
    {
        using var folder = new TestFolder();
        var song = folder.File("album/音乐_.flac");
        var cover = folder.File("album/cover.jpg");
        var output = System.IO.Path.Combine(folder.Path, "album", "out");
        folder.File("album/out/converted.m4a");
        var result = SourceScanner.Scan([System.IO.Path.GetDirectoryName(song)!, song], output);
        Assert.Single(result.Files);
        Assert.Equal(song, result.Files[0].FullPath);
        Assert.True(System.IO.File.Exists(cover));
    }

    [Fact]
    public void Unknown_extension_is_probed_not_silently_ignored()
    {
        using var folder = new TestFolder();
        folder.File("song.strange");
        Assert.Single(SourceScanner.Scan([folder.Path], "").Files);
    }

    [Fact]
    public void Explicit_attachment_is_returned_so_user_gets_reason()
    {
        using var folder = new TestFolder();
        var path = folder.File("disc.cue");
        Assert.Single(SourceScanner.Scan([path], "").Files);
    }

    [Fact]
    public void Cannot_use_input_root_as_output_and_accidentally_scan_nothing()
    {
        using var folder = new TestFolder();
        folder.File("song.flac");
        var result = SourceScanner.Scan([folder.Path], folder.Path);
        Assert.Empty(result.Files);
        Assert.NotEmpty(result.Warnings);
    }

    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("Song?.", "Song_")]
    [InlineData("..", "_")]
    [InlineData("A/B:C", "A_B_C")]
    public void Portable_filename_is_safe(string input, string expected) =>
        Assert.Equal(expected, OutputFiles.SafeSegment(input));

    [Fact]
    public void Commit_never_overwrites_and_cleans_its_temporary_file()
    {
        using var folder = new TestFolder();
        var original = folder.File("曲目.m4a", "original");
        var temp = folder.File(".mfc-temp-123.part", "converted");
        var result = OutputFiles.Commit(temp, original);
        Assert.Equal("original", System.IO.File.ReadAllText(original));
        Assert.Equal("converted", System.IO.File.ReadAllText(result));
        Assert.NotEqual(original, result);
        Assert.False(System.IO.File.Exists(temp));
    }

    [Fact]
    public void Rejects_relative_path_traversal()
    {
        using var folder = new TestFolder();
        Assert.Throws<IOException>(() => OutputFiles.Destination(
            new SourceFile("x", "../outside/song.flac"), ".m4a", new(folder.Path)));
    }

    [Fact]
    public void Volume_root_is_a_valid_output_containment_boundary()
    {
        using var folder = new TestFolder();
        var song = folder.File("song.flac");
        var root = System.IO.Path.GetPathRoot(folder.Path)!;
        var path = OutputFiles.Destination(new(song, "song.flac"), ".m4a", new(root));
        Assert.Equal(System.IO.Path.Combine(root, "song.m4a"), path);
        Assert.Empty(SourceScanner.Scan([song], root).Files);
    }
}
