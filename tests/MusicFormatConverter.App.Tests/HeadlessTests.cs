using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MusicFormatConverter.App;
using MusicFormatConverter.App.ViewModels;
using MusicFormatConverter.Core;

[assembly: AvaloniaTestApplication(typeof(MusicFormatConverter.App.Tests.HeadlessApp))]

namespace MusicFormatConverter.App.Tests;

public static class HeadlessApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<global::MusicFormatConverter.App.App>().UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public class HeadlessTests
{
    [AvaloniaTheory]
    [InlineData(1280, 820)]
    [InlineData(1050, 720)]
    public async Task Folder_import_renders_song_rows_before_output_selection(int width, int height)
    {
        var root = Path.Combine(Path.GetTempPath(), "mfc-ui-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var vm = new WorkspaceViewModel(new MusicFormatConverter.App.Services.CoreConversionService());
        var window = new MainWindow(vm) { Width = width, Height = height };
        try
        {
            foreach (var name in new[] { "歌曲一.wma", "歌曲二.flac", "歌曲三.mp3" })
                await File.WriteAllTextAsync(Path.Combine(root, name), "Only listing; no audio probe should run.");
            window.Show();
            await vm.AddInputs([root]);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(3, vm.Items.Count);
            Assert.False(window.FindControl<Border>("EmptyState")!.IsVisible);
            Assert.Equal(3, window.FindControl<ListBox>("QueueList")!.ItemCount);
            Assert.All(vm.Items, item => Assert.Equal("待预检", item.StatusText));
            var selector = window.FindControl<ComboBox>("ModeSelector")!;
            Assert.Equal(6, selector.ItemCount);
            Assert.Equal(0, selector.SelectedIndex);
            Assert.Contains("Apple Music", ((ComboBoxItem)selector.SelectedItem!).Content!.ToString());
            Assert.False(window.FindControl<Button>("PreviewButton")!.IsEnabled);
            Assert.False(window.FindControl<Button>("ConvertButton")!.IsEnabled);
            SaveScreenshot(window, $"import-before-output-{width}x{height}.png");
            vm.OutputDirectory = Path.Combine(root, "out");
            vm.ModeIndex = 4;
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.FindControl<Button>("PreviewButton")!.IsEnabled);
            Assert.False(window.FindControl<Button>("ConvertButton")!.IsEnabled);
            Assert.Equal(3, vm.Items.Count);
            Assert.False(Directory.Exists(vm.OutputDirectory));
        }
        finally { window.Close(); Directory.Delete(root, true); }
    }

    [AvaloniaTheory]
    [InlineData(false, 1280, 820)]
    [InlineData(true, 1280, 820)]
    [InlineData(false, 1050, 720)]
    [InlineData(true, 1050, 720)]
    public void Empty_window_renders_both_themes_and_sizes(bool light, int width, int height)
    {
        var vm = new WorkspaceViewModel(new WorkspaceStateTests.FakeService());
        var window = new MainWindow(vm)
        {
            Width = width,
            Height = height,
            RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark
        };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var empty = window.FindControl<Border>("EmptyState")!;
            Assert.True(empty.IsVisible);
            Assert.True(empty.Child!.Bounds.Height <= empty.Bounds.Height - empty.Padding.Top - empty.Padding.Bottom,
                "空状态内容必须完整落在卡片内，不能在最小窗口高度溢出。");
            var lastHint = empty.GetVisualDescendants().OfType<TextBlock>().Last();
            Assert.True(lastHint.TranslatePoint(new Point(0, lastHint.Bounds.Height), empty)!.Value.Y
                <= empty.Bounds.Height - empty.Padding.Bottom, "空状态最后一行必须在卡片内边距以内。");
            Assert.False(window.FindControl<Button>("PreviewButton")!.IsEnabled);
            Assert.False(window.FindControl<Button>("ConvertButton")!.IsEnabled);
            Assert.True(window.FindControl<ComboBox>("ModeSelector")!.IsEnabled);
            Assert.Equal("原样复制始终保留标签与封面。", window.FindControl<TextBlock>("MetadataHint")?.Text);
            Assert.Equal("音乐格式转换器", window.Title);
            Assert.NotNull(window.Icon);
            Assert.NotNull(window.FindControl<Image>("BrandIcon")?.Source);
            SaveScreenshot(window, $"empty-{(light ? "light" : "dark")}-{width}x{height}.png");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Queue_is_virtualized_and_preview_does_not_convert()
    {
        var plans = Enumerable.Range(1, 1000).Select(i => new PlannedFile(
            new SourceFile(Path.GetFullPath($"测试音乐-{i:0000}.flac"), $"测试音乐-{i:0000}.flac"),
            null, i == 1 ? PlanAction.Unsupported : PlanAction.EncodeAlac,
            i == 1 ? "不支持的音频规格：32 位浮点，不自动降低精度。" : "无损转换为 ALAC，保留可支持标签。",
            ".m4a", 1024)).ToArray();
        var service = new WorkspaceStateTests.FakeService { Plans = plans };
        var vm = new WorkspaceViewModel(service);
        vm.OutputDirectory = Path.GetFullPath("converted");
        await vm.AddInputs(plans.Select(p => p.Source.FullPath));
        var window = new MainWindow(vm);
        try
        {
            window.Show();
            await vm.PreviewAsync();
            Dispatcher.UIThread.RunJobs();
            var queue = window.FindControl<ListBox>("QueueList")!;
            Assert.Equal(1000, queue.ItemCount);
            Assert.InRange(queue.GetVisualDescendants().OfType<ListBoxItem>().Count(), 1, 50);
            Assert.True(window.FindControl<Button>("ConvertButton")!.IsEnabled);
            Assert.Equal(0, service.ConvertCalls);
            queue.SelectedIndex = 0;
            Dispatcher.UIThread.RunJobs();
            SaveScreenshot(window, "queue-dark-1280x820.png");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Window_close_cancels_and_waits_for_engine_cleanup()
    {
        var service = new WorkspaceStateTests.FakeService { HoldConversion = true };
        var vm = new WorkspaceViewModel(service);
        vm.OutputDirectory = Path.GetFullPath("converted");
        await vm.AddInputs([Path.GetFullPath("first.flac")]);
        var window = new MainWindow(vm);
        window.Show();
        await vm.PreviewAsync();
        var conversion = vm.ConvertAsync();
        await service.Started.Task;
        window.Close();
        await service.CancellationSeen.Task;
        Assert.True(window.IsVisible);
        Assert.True(vm.IsBusy);
        service.AllowCleanup.TrySetResult();
        await conversion;
        // Let the async Closing continuation get its turn on the dispatcher.
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Dispatcher.UIThread.RunJobs();
        Assert.True(service.CleanedUp);
        Assert.False(window.IsVisible);
    }

    [AvaloniaFact]
    public void Theme_button_changes_variant_without_invalidating_inputs()
    {
        var window = new MainWindow(new WorkspaceViewModel(new WorkspaceStateTests.FakeService()));
        try
        {
            window.Show();
            var button = window.FindControl<Button>("ThemeButton")!;
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(ThemeVariant.Light, window.RequestedThemeVariant);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(ThemeVariant.Dark, window.RequestedThemeVariant);
        }
        finally { window.Close(); }
    }

    private static void SaveScreenshot(Window window, string filename)
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        // Optional artifact output: CI/test runs otherwise stay read-only except normal build outputs.
        var directory = Environment.GetEnvironmentVariable("MFC_UI_SCREENSHOT_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        frame.Save(Path.Combine(directory, filename));
    }
}

