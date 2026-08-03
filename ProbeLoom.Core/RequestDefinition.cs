using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace ProbeLoom.Core;

public sealed class RequestDefinition : ObservableEntity
{
    private string _method = "GET";
    private string _route = "/";
    private string _rawJsonBody = string.Empty;
    private Action? _tracker;

    public AuthenticationConfiguration Authentication { get; set; } = new();

    public TokenCaptureConfiguration TokenCapture { get; set; } = new();

    public string Method
    {
        get => _method;
        set => SetProperty(ref _method, value ?? string.Empty);
    }

    public string Route
    {
        get => _route;
        set => SetProperty(ref _route, value ?? string.Empty);
    }

    public string RawJsonBody
    {
        get => _rawJsonBody;
        set => SetProperty(ref _rawJsonBody, value ?? string.Empty);
    }

    public ObservableCollection<RequestField> QueryParameters { get; set; } = [];

    public ObservableCollection<RequestField> PathParameters { get; set; } = [];

    public ObservableCollection<RequestField> Headers { get; set; } = [];

    internal override void AttachChangeTracker(Action changeTracker)
    {
        base.AttachChangeTracker(changeTracker);
        _tracker = changeTracker;
        Authentication.AttachChangeTracker(changeTracker);
        TokenCapture.AttachChangeTracker(changeTracker);
        AttachCollection(PathParameters);
        AttachCollection(QueryParameters);
        AttachCollection(Headers);
    }

    public RequestDefinition Clone() =>
        new()
        {
            Method = Method,
            Route = Route,
            RawJsonBody = RawJsonBody,
            Authentication = Authentication.Clone(),
            TokenCapture = TokenCapture.Clone(),
            PathParameters = new ObservableCollection<RequestField>(PathParameters.Select(field => field.Clone())),
            QueryParameters = new ObservableCollection<RequestField>(QueryParameters.Select(field => field.Clone())),
            Headers = new ObservableCollection<RequestField>(Headers.Select(field => field.Clone()))
        };

    private void AttachCollection(ObservableCollection<RequestField> collection)
    {
        collection.CollectionChanged -= OnCollectionChanged;
        collection.CollectionChanged += OnCollectionChanged;

        if (_tracker is not null)
        {
            foreach (var field in collection)
            {
                field.AttachChangeTracker(_tracker);
            }
        }
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (_tracker is not null && args.NewItems is not null)
        {
            foreach (var item in args.NewItems.OfType<RequestField>())
            {
                item.AttachChangeTracker(_tracker);
            }
        }

        _tracker?.Invoke();
    }
}
