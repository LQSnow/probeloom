using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace ProbeLoom.Core;

public abstract class ObservableEntity : INotifyPropertyChanged
{
    [JsonIgnore]
    private Action? _changeTracker;

    public event PropertyChangedEventHandler? PropertyChanged;

    internal virtual void AttachChangeTracker(Action changeTracker)
    {
        _changeTracker = changeTracker;
    }

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        _changeTracker?.Invoke();
        return true;
    }

    protected void NotifyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        _changeTracker?.Invoke();
    }
}
