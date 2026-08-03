namespace ProbeLoom.Core;

public sealed class RoutePart : ObservableEntity
{
    private bool _isEnabled = true;
    private string _name = string.Empty;
    private string _value = string.Empty;

    public Guid Id { get; set; } = Guid.NewGuid();

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value ?? string.Empty);
    }

    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value ?? string.Empty);
    }

    public RoutePart Clone() =>
        new()
        {
            Id = Id,
            IsEnabled = IsEnabled,
            Name = Name,
            Value = Value
        };
}
