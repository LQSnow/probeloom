using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace ProbeLoom.Core;

public enum ProjectNodeKind
{
    Group,
    Endpoint,
    RequestCase
}

public sealed class ProjectEnvironment : ObservableEntity
{
    private string _name = string.Empty;
    private string _baseUrl = string.Empty;
    private Action? _tracker;

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value ?? string.Empty);
    }

    public string BaseUrl
    {
        get => _baseUrl;
        set => SetProperty(ref _baseUrl, value ?? string.Empty);
    }

    public ObservableCollection<VariableDefinition> Variables { get; set; } = [];

    public override string ToString() => Name;

    internal override void AttachChangeTracker(Action changeTracker)
    {
        base.AttachChangeTracker(changeTracker);
        _tracker = changeTracker;
        Variables.CollectionChanged -= OnVariablesChanged;
        Variables.CollectionChanged += OnVariablesChanged;
        foreach (var variable in Variables)
        {
            variable.AttachChangeTracker(changeTracker);
        }
    }

    private void OnVariablesChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (_tracker is not null && args.NewItems is not null)
        {
            foreach (var variable in args.NewItems.OfType<VariableDefinition>())
            {
                variable.AttachChangeTracker(_tracker);
            }
        }

        _tracker?.Invoke();
    }
}

public sealed class ProjectNode : ObservableEntity
{
    private string _name = string.Empty;
    private string _routePrefix = string.Empty;
    private bool _isRoutePrefixEnabled;
    private string _summary = string.Empty;
    private string _description = string.Empty;
    private string _tags = string.Empty;
    private bool _isDeprecated;
    private Action? _tracker;

    public Guid Id { get; set; } = Guid.NewGuid();

    public ProjectNodeKind Kind { get; set; }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value ?? string.Empty);
    }

    public bool IsRoutePrefixEnabled
    {
        get => _isRoutePrefixEnabled;
        set => SetProperty(ref _isRoutePrefixEnabled, value);
    }

    public string RoutePrefix
    {
        get => _routePrefix;
        set => SetProperty(ref _routePrefix, value ?? string.Empty);
    }

    public string Summary
    {
        get => _summary;
        set => SetProperty(ref _summary, value ?? string.Empty);
    }

    public string Description
    {
        get => _description;
        set => SetProperty(ref _description, value ?? string.Empty);
    }

    public string Tags
    {
        get => _tags;
        set => SetProperty(ref _tags, value ?? string.Empty);
    }

    public bool IsDeprecated
    {
        get => _isDeprecated;
        set => SetProperty(ref _isDeprecated, value);
    }

    public RequestDefinition? Request { get; set; }

    public ObservableCollection<VariableDefinition> Variables { get; set; } = [];

    public ObservableCollection<ProjectNode> Children { get; set; } = [];

    internal override void AttachChangeTracker(Action changeTracker)
    {
        base.AttachChangeTracker(changeTracker);
        _tracker = changeTracker;
        Children.CollectionChanged -= OnChildrenChanged;
        Children.CollectionChanged += OnChildrenChanged;
        Request?.AttachChangeTracker(changeTracker);
        Variables.CollectionChanged -= OnVariablesChanged;
        Variables.CollectionChanged += OnVariablesChanged;
        foreach (var variable in Variables)
        {
            variable.AttachChangeTracker(changeTracker);
        }

        foreach (var child in Children)
        {
            child.AttachChangeTracker(changeTracker);
        }
    }

    private void OnVariablesChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (_tracker is not null && args.NewItems is not null)
        {
            foreach (var variable in args.NewItems.OfType<VariableDefinition>())
            {
                variable.AttachChangeTracker(_tracker);
            }
        }

        _tracker?.Invoke();
    }

    private void OnChildrenChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (_tracker is not null && args.NewItems is not null)
        {
            foreach (var child in args.NewItems.OfType<ProjectNode>())
            {
                child.AttachChangeTracker(_tracker);
            }
        }

        _tracker?.Invoke();
    }
}

public sealed class ProjectDocument : INotifyPropertyChanged
{
    private string _name = "Untitled project";
    private string _description = string.Empty;
    private bool _isDirty;
    private Guid? _refreshRequestNodeId;

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name
    {
        get => _name;
        set
        {
            var normalized = value ?? string.Empty;
            if (_name == normalized)
            {
                return;
            }

            _name = normalized;
            OnPropertyChanged(nameof(Name));
            MarkDirty();
        }
    }

    public string Description
    {
        get => _description;
        set
        {
            var normalized = value ?? string.Empty;
            if (_description == normalized)
            {
                return;
            }
            _description = normalized;
            OnPropertyChanged(nameof(Description));
            MarkDirty();
        }
    }

    public ObservableCollection<ProjectEnvironment> Environments { get; set; } = [];

    public ObservableCollection<VariableDefinition> Variables { get; set; } = [];

    public ObservableCollection<RoutePart> RouteParts { get; set; } = [];

    public ObservableCollection<ProjectNode> Items { get; set; } = [];

    public Guid? SelectedEnvironmentId { get; set; }

    public Guid? SelectedNodeId { get; set; }

    public Guid? RefreshRequestNodeId
    {
        get => _refreshRequestNodeId;
        set
        {
            if (_refreshRequestNodeId == value)
            {
                return;
            }

            _refreshRequestNodeId = value;
            OnPropertyChanged(nameof(RefreshRequestNodeId));
            MarkDirty();
        }
    }

    [JsonIgnore]
    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (_isDirty == value)
            {
                return;
            }

            _isDirty = value;
            OnPropertyChanged(nameof(IsDirty));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void AttachTracking()
    {
        Environments.CollectionChanged -= OnCollectionChanged;
        Environments.CollectionChanged += OnCollectionChanged;
        Variables.CollectionChanged -= OnCollectionChanged;
        Variables.CollectionChanged += OnCollectionChanged;
        RouteParts.CollectionChanged -= OnCollectionChanged;
        RouteParts.CollectionChanged += OnCollectionChanged;
        Items.CollectionChanged -= OnCollectionChanged;
        Items.CollectionChanged += OnCollectionChanged;

        foreach (var environment in Environments)
        {
            environment.AttachChangeTracker(MarkDirty);
        }

        foreach (var variable in Variables)
        {
            variable.AttachChangeTracker(MarkDirty);
        }

        foreach (var routePart in RouteParts)
        {
            routePart.AttachChangeTracker(MarkDirty);
        }

        foreach (var item in Items)
        {
            item.AttachChangeTracker(MarkDirty);
        }
    }

    public void MarkDirty() => IsDirty = true;

    public void MarkSaved() => IsDirty = false;

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (args.NewItems is not null)
        {
            foreach (var environment in args.NewItems.OfType<ProjectEnvironment>())
            {
                environment.AttachChangeTracker(MarkDirty);
            }

            foreach (var variable in args.NewItems.OfType<VariableDefinition>())
            {
                variable.AttachChangeTracker(MarkDirty);
            }

            foreach (var routePart in args.NewItems.OfType<RoutePart>())
            {
                routePart.AttachChangeTracker(MarkDirty);
            }

            foreach (var item in args.NewItems.OfType<ProjectNode>())
            {
                item.AttachChangeTracker(MarkDirty);
            }
        }

        MarkDirty();
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
