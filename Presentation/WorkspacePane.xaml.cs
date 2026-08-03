using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ProbeLoom.Core;

namespace ProbeLoom.Presentation;

public enum WorkspaceCommand
{
    AddRootGroup,
    AddNestedGroup,
    AddEndpoint,
    AddRequestCase,
    EditGroupRoute,
    EditVariables,
    RenameNode,
    DeleteNode,
    RenameProject,
    DeleteProject
}

public sealed class WorkspaceNodeEventArgs(ProjectNode? node) : EventArgs
{
    public ProjectNode? Node { get; } = node;
}

public sealed class WorkspaceCommandEventArgs(WorkspaceCommand command, ProjectNode? node) : EventArgs
{
    public WorkspaceCommand Command { get; } = command;

    public ProjectNode? Node { get; } = node;
}

public sealed class WorkspaceTreeEntry(ProjectNode node)
{
    public ProjectNode Node { get; } = node;

    public string Name => Node.Name;

    public string Glyph => Node.Kind switch
    {
        ProjectNodeKind.Group => "\uE8B7",
        ProjectNodeKind.Endpoint => "\uE774",
        ProjectNodeKind.RequestCase => "\uE7C3",
        _ => "\uE8A5"
    };

    public string KindLabel => Node.Kind switch
    {
        ProjectNodeKind.Group => "GROUP",
        ProjectNodeKind.Endpoint => "ENDPOINT",
        ProjectNodeKind.RequestCase => "CASE",
        _ => string.Empty
    };

    public string AutomationName => $"{KindLabel} {Name}";
}

public sealed partial class WorkspacePane : UserControl
{
    private ProjectDocument? _project;

    public WorkspacePane()
    {
        InitializeComponent();
    }

    public event EventHandler<WorkspaceNodeEventArgs>? NodeInvoked;

    public event EventHandler<WorkspaceCommandEventArgs>? CommandRequested;

    public void ShowProject(ProjectDocument? project, string? filePath)
    {
        _project = project;
        ProjectNameText.Text = project?.Name ?? "未打开项目";
        ProjectPathText.Text = project is null
            ? "新建或打开本地项目"
            : string.IsNullOrWhiteSpace(filePath)
                ? "尚未保存"
                : filePath;
        DirtyDot.Visibility = project?.IsDirty == true ? Visibility.Visible : Visibility.Collapsed;
        ProjectMenuButton.IsEnabled = project is not null;

        WorkspaceTree.RootNodes.Clear();
        if (project is not null)
        {
            foreach (var item in project.Items)
            {
                WorkspaceTree.RootNodes.Add(CreateTreeNode(item));
            }
        }

        EmptyStatePanel.Visibility = project is null || project.Items.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        EmptyTitleText.Text = project is null ? "新建或打开项目" : "还没有工作区内容";
        EmptyDescriptionText.Text = project is null
            ? "使用顶部工具栏创建本地项目，或打开已有的 ProbeLoom 项目文件。"
            : "先创建分组，再在其中添加 Endpoint。";
        EmptyActionButton.Visibility = project is null ? Visibility.Collapsed : Visibility.Visible;

        if (project?.SelectedNodeId is Guid selectedId)
        {
            SelectNode(selectedId);
        }
    }

    public void RefreshProjectHeader(string? filePath)
    {
        ProjectNameText.Text = _project?.Name ?? "未打开项目";
        ProjectPathText.Text = _project is null
            ? "新建或打开本地项目"
            : string.IsNullOrWhiteSpace(filePath)
                ? "尚未保存"
                : filePath;
        DirtyDot.Visibility = _project?.IsDirty == true ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SelectNode(Guid nodeId)
    {
        var node = FindTreeNode(WorkspaceTree.RootNodes, nodeId);
        if (node is null)
        {
            return;
        }

        WorkspaceTree.SelectedNode = node;
        ExpandAncestors(WorkspaceTree.RootNodes, nodeId);
        if (node.Content is WorkspaceTreeEntry entry)
        {
            NodeInvoked?.Invoke(this, new WorkspaceNodeEventArgs(entry.Node));
        }
    }

    private TreeViewNode CreateTreeNode(ProjectNode projectNode)
    {
        var treeNode = new TreeViewNode
        {
            Content = new WorkspaceTreeEntry(projectNode),
            IsExpanded = projectNode.Kind == ProjectNodeKind.Group
        };

        foreach (var child in projectNode.Children)
        {
            treeNode.Children.Add(CreateTreeNode(child));
        }

        return treeNode;
    }

    private void WorkspaceTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        var entry = sender.SelectedNode?.Content as WorkspaceTreeEntry;
        if (entry is not null)
        {
            NodeInvoked?.Invoke(this, new WorkspaceNodeEventArgs(entry.Node));
        }
    }

    private void WorkspaceRow_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TreeViewNode treeNode } ||
            treeNode.Content is not WorkspaceTreeEntry entry)
        {
            return;
        }

        WorkspaceTree.SelectedNode = treeNode;
        NodeInvoked?.Invoke(this, new WorkspaceNodeEventArgs(entry.Node));
        CreateNodeMenu(entry.Node).ShowAt((FrameworkElement)sender, e.GetPosition((FrameworkElement)sender));
        e.Handled = true;
    }

    private MenuFlyout CreateNodeMenu(ProjectNode node)
    {
        var menu = new MenuFlyout();

        if (node.Kind == ProjectNodeKind.Group)
        {
            menu.Items.Add(CreateMenuItem("新建子分组", "\uE8B7", WorkspaceCommand.AddNestedGroup, node));
            menu.Items.Add(CreateMenuItem("新建 Endpoint", "\uE710", WorkspaceCommand.AddEndpoint, node));
            menu.Items.Add(CreateMenuItem("编辑 Route Prefix", "\uE8A5", WorkspaceCommand.EditGroupRoute, node));
            menu.Items.Add(new MenuFlyoutSeparator());
        }
        else if (node.Kind == ProjectNodeKind.Endpoint)
        {
            menu.Items.Add(CreateMenuItem("新建 Request Case", "\uE8C8", WorkspaceCommand.AddRequestCase, node));
            menu.Items.Add(new MenuFlyoutSeparator());
        }

        menu.Items.Add(CreateMenuItem("编辑变量", "\uE8EF", WorkspaceCommand.EditVariables, node));
        menu.Items.Add(CreateMenuItem("重命名", "\uE8AC", WorkspaceCommand.RenameNode, node));
        var deleteItem = CreateMenuItem("删除", "\uE74D", WorkspaceCommand.DeleteNode, node);
        menu.Items.Add(deleteItem);
        return menu;
    }

    private MenuFlyoutItem CreateMenuItem(
        string text,
        string glyph,
        WorkspaceCommand command,
        ProjectNode? node)
    {
        var item = new MenuFlyoutItem
        {
            Text = text,
            Icon = new FontIcon { Glyph = glyph }
        };
        item.Click += (_, _) => CommandRequested?.Invoke(this, new WorkspaceCommandEventArgs(command, node));
        return item;
    }

    private void ProjectMenu_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null || sender is not FrameworkElement target)
        {
            return;
        }

        var menu = new MenuFlyout();
        menu.Items.Add(CreateMenuItem("重命名项目", "\uE8AC", WorkspaceCommand.RenameProject, null));
        menu.Items.Add(CreateMenuItem("编辑项目变量", "\uE8EF", WorkspaceCommand.EditVariables, null));
        menu.Items.Add(CreateMenuItem("新建根分组", "\uE8B7", WorkspaceCommand.AddRootGroup, null));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(CreateMenuItem("删除项目", "\uE74D", WorkspaceCommand.DeleteProject, null));
        menu.ShowAt(target);
    }

    private void AddRootGroup_Click(object sender, RoutedEventArgs e)
    {
        CommandRequested?.Invoke(this, new WorkspaceCommandEventArgs(WorkspaceCommand.AddRootGroup, null));
    }

    private static TreeViewNode? FindTreeNode(IList<TreeViewNode> nodes, Guid nodeId)
    {
        foreach (var node in nodes)
        {
            if (node.Content is WorkspaceTreeEntry entry && entry.Node.Id == nodeId)
            {
                return node;
            }

            var nested = FindTreeNode(node.Children, nodeId);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static bool ExpandAncestors(IList<TreeViewNode> nodes, Guid nodeId)
    {
        foreach (var node in nodes)
        {
            if (node.Content is WorkspaceTreeEntry entry && entry.Node.Id == nodeId)
            {
                return true;
            }

            if (ExpandAncestors(node.Children, nodeId))
            {
                node.IsExpanded = true;
                return true;
            }
        }

        return false;
    }
}
