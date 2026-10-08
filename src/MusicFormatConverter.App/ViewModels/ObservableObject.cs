using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MusicFormatConverter.App.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    protected void NotifyAll() => Notify(null);
}
