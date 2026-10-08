using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace MusicFormatConverter.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;
            // Handles explicit application quit (including macOS menu quit), not only window X.
            desktop.ShutdownRequested += (_, e) =>
            {
                if (!window.ViewModel.IsBusy) return;
                e.Cancel = true;
                window.Close(); // MainWindow defers closing until cancellation cleanup completes.
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
