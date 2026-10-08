using MusicFormatConverter.App.ViewModels;

namespace MusicFormatConverter.App.Tests;

public class ImportListingTests
{
    [Fact]
    public async Task Scan_is_cancellable_and_close_waits_without_allowing_concurrent_edits()
    {
        var service = new WorkspaceStateTests.FakeService { HoldScan = true };
        var vm = new WorkspaceViewModel(service);
        var importing = vm.AddInputs([Path.GetFullPath("song.mp3")]);
        await service.Started.Task;
        Assert.Equal(WorkspaceState.Scanning, vm.State);
        Assert.True(vm.IsIndeterminate);
        Assert.False(vm.CanEdit);
        Assert.False(vm.CanConvert);
        vm.ModeIndex = 4;
        Assert.Equal(0, vm.ModeIndex);
        await vm.AddInputs([Path.GetFullPath("ignored.mp3")]);
        Assert.Equal(1, vm.InputCount);
        var closing = vm.CancelAndWaitAsync();
        await service.CancellationSeen.Task;
        Assert.False(closing.IsCompleted);
        service.AllowCleanup.TrySetResult();
        await closing;
        await importing;
        Assert.False(vm.IsBusy);
        Assert.False(vm.CanEdit);
        Assert.Contains("取消", vm.StatusMessage);
        Assert.Equal(0, service.ConvertCalls);
    }

    [Fact]
    public async Task Scan_error_is_visible_and_can_be_cleared()
    {
        var vm = new WorkspaceViewModel(new WorkspaceStateTests.FakeService { ScanError = new IOException("测试目录不可读") });
        await vm.AddInputs([Path.GetFullPath("folder")]);
        Assert.False(vm.IsBusy);
        Assert.Contains("不可读", vm.StatusMessage);
        Assert.False(vm.CanConvert);
        vm.Clear();
        Assert.Equal(0, vm.InputCount);
        Assert.Empty(vm.Items);
    }

    [Theory]
    [InlineData(0, "Apple Music")]
    [InlineData(1, "ALAC")]
    [InlineData(2, "AAC")]
    [InlineData(3, "MP3")]
    [InlineData(4, "FLAC")]
    [InlineData(5, "WAV")]
    public void Output_choices_do_not_change_default_or_silently_enable_conversion(int index, string description)
    {
        var vm = new WorkspaceViewModel(new WorkspaceStateTests.FakeService());
        Assert.Equal(0, vm.ModeIndex);
        Assert.Contains("Apple Music", vm.ModeDescription);
        vm.ModeIndex = index;
        Assert.Equal(index, vm.ModeIndex);
        Assert.Contains(description, vm.ModeDescription);
        Assert.False(vm.CanConvert);
    }

    [Fact]
    public async Task Folder_import_lists_files_without_output_or_audio_engine()
    {
        var path = Path.Combine(Path.GetTempPath(), "mfc-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(path, "album"));
        try
        {
            File.WriteAllText(Path.Combine(path, "one.wma"), "test");
            File.WriteAllText(Path.Combine(path, "two.flac"), "test");
            File.WriteAllText(Path.Combine(path, "album", "three.mp3"), "test");
            File.WriteAllText(Path.Combine(path, "cover.jpg"), "not audio");
            var service = new WorkspaceStateTests.FakeService();
            var vm = new WorkspaceViewModel(service);
            await vm.AddInputs([path, Path.Combine(path, "one.wma")]);
            Assert.Equal(3, vm.Items.Count);
            Assert.DoesNotContain(vm.Items, item => Directory.Exists(item.SourcePath));
            Assert.Equal(2, vm.InputCount);
            Assert.False(vm.CanConvert);
            Assert.False(vm.CanPreview);
            Assert.Equal(0, service.ConvertCalls);
            vm.OutputDirectory = Path.Combine(path, "out");
            vm.ModeIndex = 1;
            Assert.Equal(3, vm.Items.Count);
            Assert.True(vm.CanPreview);
            Assert.False(Directory.Exists(vm.OutputDirectory));
            await vm.AddInputs([path]); // Duplicate sources do not duplicate rows.
            Assert.Equal(3, vm.Items.Count);
            vm.PreserveMetadata = false;
            vm.VerifyAudio = false;
            vm.PreserveFolders = false;
            Assert.Equal(3, vm.Items.Count);
            vm.Clear();
            Assert.Empty(vm.Items);
        }
        finally { Directory.Delete(path, true); }
    }

    [Fact]
    public async Task Empty_folder_does_not_create_a_fake_song_and_output_tree_is_excluded()
    {
        var path = Path.Combine(Path.GetTempPath(), "mfc-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            var vm = new WorkspaceViewModel(new WorkspaceStateTests.FakeService());
            await vm.AddInputs([path]);
            Assert.Empty(vm.Items);
            Assert.Contains("没有候选", vm.StatusMessage);
            vm.Clear();
            var output = Path.Combine(path, "out");
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "old.mp3"), "test");
            File.WriteAllText(Path.Combine(path, "new.flac"), "test");
            vm.OutputDirectory = output;
            await vm.AddInputs([path]);
            Assert.Equal("new.flac", Assert.Single(vm.Items).Name);
        }
        finally { Directory.Delete(path, true); }
    }
}

