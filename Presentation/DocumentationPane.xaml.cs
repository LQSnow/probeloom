using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProbeLoom.Core;
using Windows.ApplicationModel.DataTransfer;

namespace ProbeLoom.Presentation;

public sealed class MarkdownExportRequestedEventArgs(string markdown, string suggestedName) : EventArgs
{
    public string Markdown { get; } = markdown;
    public string SuggestedName { get; } = suggestedName;
}

public sealed partial class DocumentationPane : UserControl
{
    private RouteCatalog? _catalog;
    private ProjectDocument? _project;
    private bool _loading;

    public DocumentationPane() => InitializeComponent();

    public event EventHandler? MetadataChanged;
    public event EventHandler<MarkdownExportRequestedEventArgs>? ExportRequested;

    public void ShowCatalog(ProjectDocument? project, RouteCatalog? catalog, Guid? selectedNodeId)
    {
        _project = project;
        _catalog = catalog;
        _loading = true;
        var scopes = new List<ScopeItem> { new(DocumentationScope.Project, null, "Entire project") };
        if (project is not null)
        {
            scopes.AddRange(ProjectOperations.EnumerateNodes(project.Items)
                .Where(node => node.Kind is ProjectNodeKind.Group or ProjectNodeKind.Endpoint)
                .Select(node => new ScopeItem(
                    node.Kind == ProjectNodeKind.Group ? DocumentationScope.Group : DocumentationScope.Endpoint,
                    node.Id,
                    $"{node.Kind} · {node.Name}")));
        }
        ScopeComboBox.ItemsSource = scopes;
        ScopeComboBox.SelectedItem = scopes.FirstOrDefault(item => item.NodeId == selectedNodeId) ?? scopes[0];
        _loading = false;
        RefreshPreview();
    }

    private void ScopeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading) RefreshPreview();
    }

    private void RefreshPreview()
    {
        if (_catalog is null || _project is null || ScopeComboBox.SelectedItem is not ScopeItem scope)
        {
            MarkdownTextBox.Text = string.Empty;
            return;
        }
        var node = scope.NodeId is Guid id ? ProjectOperations.FindNode(_project, id) : null;
        SummaryTextBox.IsEnabled = node?.Kind == ProjectNodeKind.Endpoint;
        TagsTextBox.IsEnabled = node?.Kind == ProjectNodeKind.Endpoint;
        DeprecatedCheckBox.IsEnabled = node?.Kind == ProjectNodeKind.Endpoint;
        SummaryTextBox.Text = node?.Summary ?? string.Empty;
        DescriptionTextBox.Text = node?.Description ?? _project.Description;
        TagsTextBox.Text = node?.Tags ?? string.Empty;
        DeprecatedCheckBox.IsChecked = node?.IsDeprecated == true;
        MarkdownTextBox.Text = ApiDocumentationMarkdownGenerator.Generate(
            _catalog, scope.Scope, scope.NodeId);
    }

    private void ApplyMetadata_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null || ScopeComboBox.SelectedItem is not ScopeItem scope) return;
        var node = scope.NodeId is Guid id ? ProjectOperations.FindNode(_project, id) : null;
        if (node is null)
        {
            _project.Description = DescriptionTextBox.Text;
        }
        else
        {
            node.Summary = SummaryTextBox.Text;
            node.Description = DescriptionTextBox.Text;
            node.Tags = TagsTextBox.Text;
            node.IsDeprecated = DeprecatedCheckBox.IsChecked == true;
        }
        MetadataChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(MarkdownTextBox.Text);
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }

    private void Export_Click(object sender, RoutedEventArgs e) =>
        ExportRequested?.Invoke(
            this,
            new MarkdownExportRequestedEventArgs(
                MarkdownTextBox.Text,
                $"{_project?.Name ?? "ProbeLoom API"}.md"));

    private sealed record ScopeItem(DocumentationScope Scope, Guid? NodeId, string Label);
}
