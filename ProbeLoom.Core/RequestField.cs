namespace ProbeLoom.Core;

public sealed class RequestField : ObservableEntity
{
    private bool _isEnabled = true;
    private string _name = string.Empty;
    private string _value = string.Empty;
    private string _description = string.Empty;

    public RequestField()
    {
    }

    public RequestField(string name, string value, bool isEnabled = true)
    {
        _name = name;
        _value = value;
        _isEnabled = isEnabled;
    }

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

    public string Description
    {
        get => _description;
        set => SetProperty(ref _description, value ?? string.Empty);
    }

    public RequestField Clone() => new(Name, Value, IsEnabled) { Description = Description };
}
