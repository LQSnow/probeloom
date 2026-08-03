using System.Text.Json.Serialization;

namespace ProbeLoom.Core;

public sealed class VariableDefinition : ObservableEntity
{
    private bool _isEnabled = true;
    private bool _isSecret;
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

    public bool IsSecret
    {
        get => _isSecret;
        set
        {
            if (SetProperty(ref _isSecret, value) && value)
            {
                _value = string.Empty;
                NotifyChanged(nameof(Value));
            }
        }
    }

    [JsonIgnore]
    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value ?? string.Empty);
    }

    [JsonPropertyName("value")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PersistedValue
    {
        get => IsSecret ? null : Value;
        set
        {
            if (!IsSecret)
            {
                Value = value ?? string.Empty;
            }
        }
    }

    public VariableDefinition Clone() =>
        new()
        {
            Id = Id,
            IsEnabled = IsEnabled,
            Name = Name,
            IsSecret = IsSecret,
            PersistedValue = IsSecret ? null : Value
        };

}
