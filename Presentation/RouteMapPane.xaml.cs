using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProbeLoom.Core;

namespace ProbeLoom.Presentation;

public sealed class RouteCatalogEntryInvokedEventArgs(Guid nodeId) : EventArgs
{
    public Guid NodeId { get; } = nodeId;
}

public sealed partial class RouteMapPane : UserControl
{
    private RouteCatalog? _catalog;
    private bool _initializing = true;

    public RouteMapPane()
    {
        InitializeComponent();
        MethodFilter.ItemsSource = new[] { "All methods", "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" };
        MethodFilter.SelectedIndex = 0;
        GroupFilter.ItemsSource = new[] { "All groups" };
        GroupFilter.SelectedIndex = 0;
        _initializing = false;
    }

    public event EventHandler<RouteCatalogEntryInvokedEventArgs>? EntryInvoked;
    public event EventHandler? RefreshRequested;

    public void ShowCatalog(RouteCatalog? catalog)
    {
        _catalog = catalog;
        var groups = catalog?.Entries.SelectMany(entry => entry.GroupPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Prepend("All groups")
            .ToArray() ?? ["All groups"];
        var previous = GroupFilter.SelectedItem as string;
        GroupFilter.ItemsSource = groups;
        GroupFilter.SelectedItem = groups.Contains(previous) ? previous : groups[0];
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var entries = _catalog?.Entries.AsEnumerable() ?? [];
        var query = SearchBox.Text?.Trim() ?? string.Empty;
        var method = MethodFilter.SelectedItem as string;
        var group = GroupFilter.SelectedItem as string;
        if (!string.IsNullOrWhiteSpace(query))
            entries = entries.Where(entry =>
                entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                entry.RouteTemplate.Contains(query, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(method) && method != "All methods")
            entries = entries.Where(entry => entry.Method == method);
        if (!string.IsNullOrWhiteSpace(group) && group != "All groups")
            entries = entries.Where(entry => entry.GroupPath.Contains(group, StringComparer.OrdinalIgnoreCase));
        if (ShowCasesCheckBox.IsChecked != true)
            entries = entries.Where(entry => entry.Kind != ProjectNodeKind.RequestCase);

        var conflicts = _catalog?.Conflicts.SelectMany(item => item.NodeIds).ToHashSet() ?? [];
        var views = entries.Select(entry => new EntryView(
            entry,
            !entry.IsValid ? "Invalid" : conflicts.Contains(entry.NodeId) ? "Conflict" : string.Empty)).ToArray();
        EntriesList.ItemsSource = views;
        SummaryText.Text = _catalog is null
            ? "打开项目后生成 Route Catalog。"
            : $"{views.Length} shown · {_catalog.Entries.Count} requests · {_catalog.Conflicts.Count} issues";
    }

    private void Filter_Changed(object sender, object e)
    {
        if (!_initializing)
        {
            ApplyFilter();
        }
    }
    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke(this, EventArgs.Empty);

    private void EntriesList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is EntryView view)
            EntryInvoked?.Invoke(this, new RouteCatalogEntryInvokedEventArgs(view.NodeId));
    }

    private sealed record EntryView(RouteCatalogEntry Entry, string Status)
    {
        public Guid NodeId => Entry.NodeId;
        public string Method => Entry.Method;
        public string Name => Entry.Name;
        public string Kind => Entry.Kind.ToString();
        public string RouteTemplate => Entry.RouteTemplate;
        public string ExampleUrl => Entry.ExampleUrl;
        public string GroupDisplay => Entry.GroupDisplay;
        public string Authentication => Entry.Authentication;
    }
}
