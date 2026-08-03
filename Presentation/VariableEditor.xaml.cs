using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProbeLoom.Core;

namespace ProbeLoom.Presentation;

public sealed partial class VariableEditor : UserControl
{
    private readonly ObservableCollection<VariableEditorItem> _items = [];
    private readonly HashSet<Guid> _originalIds = [];
    private Guid _projectId;
    private ISecureValueStore? _secureValueStore;

    public VariableEditor()
    {
        InitializeComponent();
        VariablesList.ItemsSource = _items;
    }

    public async Task LoadAsync(
        Guid projectId,
        IEnumerable<VariableDefinition> variables,
        ISecureValueStore secureValueStore)
    {
        _projectId = projectId;
        _secureValueStore = secureValueStore;
        _items.Clear();
        _originalIds.Clear();
        foreach (var variable in variables)
        {
            _originalIds.Add(variable.Id);
            var value = variable.IsSecret
                ? await secureValueStore.GetAsync(SecureValueKeys.Variable(projectId, variable.Id)) ?? string.Empty
                : variable.Value;
            _items.Add(new VariableEditorItem
            {
                Id = variable.Id,
                IsEnabled = variable.IsEnabled,
                Name = variable.Name,
                IsSecret = variable.IsSecret,
                Value = value
            });
        }
        UpdateEmptyState();
    }

    public async Task<OperationResult<IReadOnlyList<VariableDefinition>>> ApplyAsync()
    {
        if (_secureValueStore is null)
        {
            return OperationResult<IReadOnlyList<VariableDefinition>>.Failure("安全存储尚未初始化。");
        }

        var enabledNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in _items)
        {
            var name = item.Name.Trim();
            if (!Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_.-]*$") ||
                name.StartsWith("token.", StringComparison.OrdinalIgnoreCase))
            {
                return OperationResult<IReadOnlyList<VariableDefinition>>.Failure(
                    $"变量名“{name}”无效；请使用字母或下划线开头，token.* 为保留命名空间。");
            }

            if (item.IsEnabled && !enabledNames.Add(name))
            {
                return OperationResult<IReadOnlyList<VariableDefinition>>.Failure(
                    $"启用的变量“{name}”重复。");
            }
        }

        var currentIds = _items.Select(item => item.Id).ToHashSet();
        foreach (var removedId in _originalIds.Where(id => !currentIds.Contains(id)))
        {
            await _secureValueStore.RemoveAsync(SecureValueKeys.Variable(_projectId, removedId));
        }

        var definitions = new List<VariableDefinition>();
        foreach (var item in _items)
        {
            var definition = new VariableDefinition
            {
                Id = item.Id,
                IsEnabled = item.IsEnabled,
                Name = item.Name.Trim(),
                IsSecret = item.IsSecret
            };
            if (item.IsSecret)
            {
                await _secureValueStore.SetAsync(
                    SecureValueKeys.Variable(_projectId, item.Id),
                    item.Value);
            }
            else
            {
                definition.Value = item.Value;
                await _secureValueStore.RemoveAsync(SecureValueKeys.Variable(_projectId, item.Id));
            }
            definitions.Add(definition);
        }

        return OperationResult<IReadOnlyList<VariableDefinition>>.Success(definitions);
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var item = new VariableEditorItem { Name = GetUniqueDefaultName(), IsEnabled = true };
        _items.Add(item);
        VariablesList.SelectedItem = item;
        VariablesList.ScrollIntoView(item);
        UpdateEmptyState();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: VariableEditorItem item })
        {
            _items.Remove(item);
            UpdateEmptyState();
        }
    }

    private string GetUniqueDefaultName()
    {
        var suffix = 1;
        var name = "variable";
        while (_items.Any(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            name = $"variable{++suffix}";
        }
        return name;
    }

    private void UpdateEmptyState() =>
        EmptyState.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private sealed class VariableEditorItem : INotifyPropertyChanged
    {
        private bool _isEnabled;
        private bool _isSecret;
        private string _name = string.Empty;
        private string _value = string.Empty;

        public Guid Id { get; set; } = Guid.NewGuid();

        public bool IsEnabled
        {
            get => _isEnabled;
            set => Set(ref _isEnabled, value);
        }

        public bool IsSecret
        {
            get => _isSecret;
            set
            {
                if (Set(ref _isSecret, value) && !value)
                {
                    Value = string.Empty;
                }
            }
        }

        public string Name
        {
            get => _name;
            set => Set(ref _name, value ?? string.Empty);
        }

        public string Value
        {
            get => _value;
            set => Set(ref _value, value ?? string.Empty);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            return true;
        }
    }
}
