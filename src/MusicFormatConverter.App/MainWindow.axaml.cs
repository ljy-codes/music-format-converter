using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using MusicFormatConverter.App.Services;
using MusicFormatConverter.App.ViewModels;

namespace MusicFormatConverter.App;

public partial class MainWindow : Window
{
    private bool _closeApproved;
    private bool _closing;

    public WorkspaceViewModel ViewModel { get; }

    public MainWindow() : this(new WorkspaceViewModel(new CoreConversionService(),
        action => Dispatcher.UIThread.Post(action))) { }

    public MainWindow(WorkspaceViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        DataContext = ViewModel;
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        Closing += OnClosing;
    }

    private async void ImportFiles(object? sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        if (!ViewModel.CanEdit) return;
        if (!StorageProvider.CanOpen) { ViewModel.ShowError("当前系统不支持文件选择，请尝试拖入本地文件。"); return; }
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "添加音乐文件",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("音频文件")
                {
                    Patterns = ["*.flac", "*.wav", "*.wave", "*.mp3", "*.m4a", "*.aac", "*.aiff", "*.aif",
                        "*.ogg", "*.oga", "*.opus", "*.wma", "*.ape", "*.alac", "*.dsf", "*.dff"]
                },
                FilePickerFileTypes.All
            ]
        });
        await AddStorageItems(files);
    });

    private async void ImportFolders(object? sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        if (!ViewModel.CanEdit) return;
        if (!StorageProvider.CanPickFolder) { ViewModel.ShowError("当前系统不支持文件夹选择，请尝试拖入本地文件夹。"); return; }
        await AddStorageItems(await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "添加音乐文件夹",
            AllowMultiple = true
        }));
    });

    private async void ChooseOutput(object? sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        if (!ViewModel.CanEdit) return;
        var directory = await PickFolderAsync("选择输出文件夹");
        if (directory is not null) ViewModel.OutputDirectory = directory;
    });

    private async Task<string?> PickFolderAsync(string title)
    {
        if (!StorageProvider.CanPickFolder)
        {
            ViewModel.ShowError("当前系统不支持文件夹选择。");
            return null;
        }
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title, AllowMultiple = false
        });
        try
        {
            var path = folders.FirstOrDefault()?.TryGetLocalPath();
            if (folders.Count > 0 && path is null) ViewModel.ShowError("请选择本地文件夹，不支持虚拟或云端路径。");
            return path;
        }
        finally { foreach (var folder in folders) folder.Dispose(); }
    }

    private async Task AddStorageItems(IEnumerable<IStorageItem> items)
    {
        var paths = new List<string>();
        var unavailable = 0;
        foreach (var item in items)
        {
            using (item)
            {
                var path = item.TryGetLocalPath();
                if (path is not null) paths.Add(path);
                else unavailable++;
            }
        }
        await ViewModel.AddInputs(paths);
        if (unavailable > 0) ViewModel.ShowError($"有 {unavailable} 项不是本地路径，未添加。");
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = ViewModel.CanEdit && e.DataTransfer.Contains(DataFormat.File)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!ViewModel.CanEdit) return;
        try
        {
            var files = e.DataTransfer.TryGetFiles();
            if (files is not null) await AddStorageItems(files);
        }
        catch (Exception ex) { ShowUiException(ex); }
    }

    private async void Preview(object? sender, RoutedEventArgs e) => await GuardAsync(ViewModel.PreviewAsync);
    private async void Convert(object? sender, RoutedEventArgs e) => await GuardAsync(ViewModel.ConvertAsync);
    private async void RetryFailed(object? sender, RoutedEventArgs e) => await GuardAsync(ViewModel.RetryFailedAsync);
    private void ClearQueue(object? sender, RoutedEventArgs e) => ViewModel.Clear();
    private void CancelWork(object? sender, RoutedEventArgs e) => ViewModel.RequestCancel();

    private async void ExportReport(object? sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        if (!ViewModel.CanExport) return;
        var directory = await PickFolderAsync("选择报告保存文件夹");
        if (directory is not null) await ViewModel.ExportReportAsync(directory);
    });

    private void OpenOutput(object? sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanOpenOutput) return;
        try
        {
            // This is the only shell action. It is an explicit user action on a validated local directory,
            // never a source file, metadata URL, engine command or automatically returned report path.
            var folder = Path.GetFullPath(ViewModel.OutputDirectory);
            if (!Directory.Exists(folder)) { ViewModel.ShowError("输出文件夹尚不存在，转换后再打开。"); return; }
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex) { ShowUiException(ex); }
    }

    private void ToggleTheme(object? sender, RoutedEventArgs e)
    {
        var light = ActualThemeVariant != ThemeVariant.Light;
        RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        ThemeButton.Content = light ? "切换深色" : "切换浅色";
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeApproved || !ViewModel.IsBusy) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        try
        {
            await ViewModel.CancelAndWaitAsync();
            _closeApproved = true;
            Close();
        }
        catch (Exception ex)
        {
            _closing = false;
            ShowUiException(ex);
        }
    }

    private async Task GuardAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { ShowUiException(ex); }
    }

    private void ShowUiException(Exception ex)
    {
        var message = ex.Message.Replace('\r', ' ').Replace('\n', ' ');
        ViewModel.ShowError("操作未完成：" + (message.Length > 200 ? message[..200] + "…" : message));
    }
}
