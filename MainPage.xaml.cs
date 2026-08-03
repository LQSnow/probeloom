using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using ProbeLoom.Core;
using ProbeLoom.Presentation;
using ProbeLoom.Services;
using Windows.Storage.Pickers;
using Windows.System;

namespace ProbeLoom;

public sealed partial class MainPage : Page
{
    private readonly ProjectLifecycleService _session = new();
    private readonly HttpRequestExecutor _httpExecutor = new();
    private readonly RequestHistory _requestHistory = new(30);
    private readonly ISecureValueStore _secureValueStore = new WindowsDataProtectionSecretStore();
    private readonly TokenSessionService _tokenSessionService;
    private readonly RequestExecutionService _requestExecutionService;
    private readonly RouteCatalogService _routeCatalogService;
    private readonly InspectorPreferences _inspectorPreferences = new();
    private readonly NetworkDiagnosticService _networkDiagnostics = new();
    private readonly DiagnosticResultStore _diagnosticResults = new();
    private InspectorLayoutState _inspectorLayout;
    private ProjectNode? _selectedNode;
    private ProjectEnvironment? _selectedEnvironment;
    private CancellationTokenSource? _requestCancellation;
    private CancellationTokenSource? _diagnosticCancellation;
    private Guid? _activeRequestNodeId;
    private string? _activeRequestName;
    private string? _activeRequestUrl;
    private bool _isLoading;
    private TokenSession? _tokenSession;
    private int _previewVersion;
    private int _tokenContextVersion;
    private WorkspaceMode _workspaceMode;
    private GridLength _requestDiagnosticsHeight = new(220);

    public MainPage()
    {
        InitializeComponent();
        _tokenSessionService = new TokenSessionService(
            _secureValueStore,
            _httpExecutor,
            _requestHistory);
        _requestExecutionService = new RequestExecutionService(
            _secureValueStore,
            _httpExecutor,
            _requestHistory,
            _tokenSessionService);
        _routeCatalogService = new RouteCatalogService(_secureValueStore);
        _inspectorLayout = _inspectorPreferences.Load();
        DiagnosticsPane.SetHistory(_requestHistory);
        ApplyInspectorLayout();
        SetWorkspaceMode(WorkspaceMode.Request);
    }

    public async Task<bool> ConfirmCloseAsync()
    {
        var canClose = await ConfirmUnsavedChangesAsync("关闭 ProbeLoom");
        if (canClose)
        {
            _requestCancellation?.Cancel();
            _diagnosticCancellation?.Cancel();
            _routeCatalogService.Cancel();
        }

        return canClose;
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        var result = await _session.RestoreLastProjectAsync();
        ApplyProjectTransition(result.Transition);

        if (result.Error is not null)
        {
            var message = result.Warning is null
                ? result.Error
                : $"{result.Error}\r\n\r\n{result.Warning}";
            await ShowErrorAsync("无法恢复项目", message);
        }
        else if (result.Warning is not null)
        {
            DiagnosticsPane.ShowMessage("最近项目记录未清理", result.Warning, isError: true);
        }
    }

    private void ApplyProjectTransition(ProjectTransition transition)
    {
        CancelActiveRequest();
        _requestCancellation = null;
        _diagnosticCancellation = null;
        _activeRequestNodeId = null;
        _activeRequestName = null;
        _activeRequestUrl = null;
        _routeCatalogService.Cancel();
        _previewVersion++;
        _tokenContextVersion++;

        if (transition.PreviousProject is not null)
        {
            transition.PreviousProject.PropertyChanged -= Project_PropertyChanged;
        }
        if (transition.CurrentProject is not null)
        {
            transition.CurrentProject.PropertyChanged += Project_PropertyChanged;
        }

        _selectedNode = null;
        _selectedEnvironment = null;
        _tokenSession = null;
        _diagnosticResults.Clear();
        RouteMapPane.ShowCatalog(null);
        DocumentationPane.ShowCatalog(null, null, null);
        InspectorPane.SetDiagnosticsRunning(false);
        DiagnosticsPane.Reset();
        RefreshWorkspace();
    }

    private void Project_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ProjectDocument.IsDirty) or nameof(ProjectDocument.Name))
        {
            RefreshProjectChrome();
        }
    }

    private void RefreshWorkspace()
    {
        _isLoading = true;
        var project = _session.Project;
        WorkspacePane.ShowProject(project, _session.ProjectFilePath);
        EnvironmentComboBox.ItemsSource = project?.Environments;

        _selectedEnvironment = project?.SelectedEnvironmentId is Guid environmentId
            ? project.Environments.FirstOrDefault(environment => environment.Id == environmentId)
            : project?.Environments.FirstOrDefault();
        EnvironmentComboBox.SelectedItem = _selectedEnvironment;
        BaseUrlTextBox.Text = _selectedEnvironment?.BaseUrl ?? string.Empty;

        _selectedNode = project?.SelectedNodeId is Guid nodeId
            ? ProjectOperations.FindNode(project, nodeId)
            : null;
        _diagnosticResults.Select(_selectedNode?.Request is null ? null : _selectedNode.Id);
        _isLoading = false;

        RequestEditor.ShowRequest(project, _selectedNode, _selectedEnvironment);
        if (_selectedNode?.Request is null)
        {
            InspectorPane.ShowSnapshot(null);
        }
        InspectorPane.ShowDiagnostics(_diagnosticResults.Current());
        RequestEditor.SetExecutionState(_requestCancellation is not null);
        RefreshDisplayedResponse();
        RefreshProjectChrome();
        RefreshCommandAvailability();
        ValidateEnvironmentInline();
        _ = LoadTokenAndPreviewAsync();
        if (_workspaceMode != WorkspaceMode.Request)
        {
            _ = RefreshRouteCatalogAsync(RouteCatalogRefreshMode.Immediate);
        }
    }

    private void RefreshProjectChrome()
    {
        var project = _session.Project;
        WorkspacePane.RefreshProjectHeader(_session.ProjectFilePath);
        (Application.Current as App)?.MainWindowInstance?.UpdateProjectTitle(project?.Name, project?.IsDirty == true);
        SaveButton.IsEnabled = project is not null;
        SaveAsButton.IsEnabled = project is not null;
    }

    private void RefreshCommandAvailability()
    {
        var hasProject = _session.Project is not null;
        AddEnvironmentButton.IsEnabled = hasProject;
        EditEnvironmentButton.IsEnabled = _selectedEnvironment is not null;
        DeleteEnvironmentButton.IsEnabled = _selectedEnvironment is not null;
        BaseUrlTextBox.IsEnabled = _selectedEnvironment is not null;
        ProjectRoutesButton.IsEnabled = hasProject;
        EnvironmentVariablesButton.IsEnabled = hasProject;
        EnvironmentVariableMenuItem.IsEnabled = _selectedEnvironment is not null;
        TokenSessionButton.IsEnabled = _selectedEnvironment is not null;
        RefreshTokenButton.IsEnabled =
            _selectedEnvironment is not null && _session.Project?.RefreshRequestNodeId is not null;

        var selectedGroup = GetSelectedGroup();
        NewEndpointButton.IsEnabled = selectedGroup is not null;
        NewCaseButton.IsEnabled = GetSelectedEndpoint() is not null;
    }

    private async void NewProject_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmUnsavedChangesAsync("新建项目"))
        {
            return;
        }

        var name = await PromptForNameAsync("新建本地项目", "项目名称", "例如：订单服务 API");
        if (name is null)
        {
            return;
        }

        var result = await _session.CreateProjectAsync(name);
        ApplyProjectTransition(result.Transition);
        if (result.Warning is not null)
        {
            DiagnosticsPane.ShowMessage("项目已创建", result.Warning, isError: true);
        }
    }

    private async void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmUnsavedChangesAsync("打开其他项目"))
        {
            return;
        }

        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".json");
        InitializePicker(picker);
        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        try
        {
            var result = await _session.OpenProjectAsync(file.Path);
            ApplyProjectTransition(result.Transition);
            DiagnosticsPane.ShowMessage("项目已打开", $"已从本地打开：\r\n{file.Path}");
        }
        catch (ProjectFileException exception)
        {
            await ShowErrorAsync("无法打开项目", exception.Message);
        }
    }

    private async void SaveProject_Click(object sender, RoutedEventArgs e)
    {
        await SaveProjectAsync(forceChoosePath: false);
    }

    private async void SaveProjectAs_Click(object sender, RoutedEventArgs e)
    {
        await SaveProjectAsync(forceChoosePath: true);
    }

    private async Task<bool> SaveProjectAsync(bool forceChoosePath)
    {
        if (_session.Project is null)
        {
            return false;
        }

        string? path = forceChoosePath ? null : _session.ProjectFilePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            var picker = new FileSavePicker
            {
                SuggestedFileName = $"{SanitizeFileName(_session.Project.Name)}.probeloom"
            };
            picker.FileTypeChoices.Add("ProbeLoom 项目", [".json"]);
            InitializePicker(picker);
            var file = await picker.PickSaveFileAsync();
            if (file is null)
            {
                return false;
            }

            path = file.Path;
        }

        try
        {
            var result = await _session.SaveProjectAsync(path);
            RefreshProjectChrome();
            DiagnosticsPane.ShowMessage(
                result.Warning is null ? "已保存" : "已保存，但最近项目记录失败",
                result.Warning is null
                    ? $"项目已保存到：\r\n{result.FilePath}"
                    : $"项目已保存到：\r\n{result.FilePath}\r\n\r\n{result.Warning}",
                isError: result.Warning is not null);
            return true;
        }
        catch (ProjectFileException exception)
        {
            await ShowErrorAsync("保存失败", exception.Message);
            return false;
        }
    }

    private void EnvironmentComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || _session.Project is null)
        {
            return;
        }

        _selectedEnvironment = EnvironmentComboBox.SelectedItem as ProjectEnvironment;
        _session.Project.SelectedEnvironmentId = _selectedEnvironment?.Id;
        _isLoading = true;
        BaseUrlTextBox.Text = _selectedEnvironment?.BaseUrl ?? string.Empty;
        _isLoading = false;
        RefreshCommandAvailability();
        ValidateEnvironmentInline();
        _ = LoadTokenAndPreviewAsync();
        if (_workspaceMode != WorkspaceMode.Request)
        {
            _ = RefreshRouteCatalogAsync(RouteCatalogRefreshMode.Immediate);
        }
    }

    private void BaseUrlTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isLoading && _selectedEnvironment is not null)
        {
            _selectedEnvironment.BaseUrl = BaseUrlTextBox.Text;
            _ = RefreshPreparedPreviewAsync();
            if (_workspaceMode != WorkspaceMode.Request)
            {
                _ = RefreshRouteCatalogAsync();
            }
        }

        ValidateEnvironmentInline();
    }

    private void ValidateEnvironmentInline()
    {
        string? message = null;
        if (_session.Project is not null && _selectedEnvironment is null)
        {
            message = "请先创建或选择 Environment。";
        }
        else if (_selectedEnvironment is not null)
        {
            if (string.IsNullOrWhiteSpace(BaseUrlTextBox.Text))
            {
                message = "当前 Environment 尚未配置 Base URL。";
            }
            else if (BaseUrlTextBox.Text.Contains("{{", StringComparison.Ordinal))
            {
                // The unified request preparation pipeline validates the resolved URL.
            }
            else if (!Uri.TryCreate(BaseUrlTextBox.Text.Trim(), UriKind.Absolute, out var uri) ||
                     (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                message = "Base URL 必须是完整的 HTTP 或 HTTPS 地址。";
            }
            else if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            {
                message = "请将 Query 和 Fragment 放到请求编辑区，而不是 Base URL。";
            }
        }

        EnvironmentHintText.Text = message ?? string.Empty;
        EnvironmentHintText.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void AddEnvironment_Click(object sender, RoutedEventArgs e)
    {
        if (_session.Project is null)
        {
            return;
        }

        var input = await PromptForEnvironmentAsync("新建 Environment", null);
        if (input is null)
        {
            return;
        }

        var result = ProjectOperations.AddEnvironment(_session.Project, input.Value.Name, input.Value.BaseUrl);
        if (!result.Succeeded)
        {
            await ShowErrorAsync("无法创建 Environment", result.Error!);
            return;
        }

        _selectedEnvironment = result.Value;
        RefreshWorkspace();
    }

    private async void EditEnvironment_Click(object sender, RoutedEventArgs e)
    {
        if (_session.Project is null || _selectedEnvironment is null)
        {
            return;
        }

        var input = await PromptForEnvironmentAsync("编辑 Environment", _selectedEnvironment);
        if (input is null)
        {
            return;
        }

        var result = ProjectOperations.UpdateEnvironment(
            _session.Project,
            _selectedEnvironment.Id,
            input.Value.Name,
            input.Value.BaseUrl);
        if (!result.Succeeded)
        {
            await ShowErrorAsync("无法修改 Environment", result.Error!);
            return;
        }

        RefreshWorkspace();
    }

    private async void DeleteEnvironment_Click(object sender, RoutedEventArgs e)
    {
        if (_session.Project is null || _selectedEnvironment is null)
        {
            return;
        }

        var environment = _selectedEnvironment;
        if (!await ConfirmDeleteAsync(
                "删除 Environment",
                $"确定删除“{environment.Name}”吗？该 Environment 的 Base URL 将被移除。"))
        {
            return;
        }

        var result = ProjectOperations.DeleteEnvironment(_session.Project, environment.Id);
        if (!result.Succeeded)
        {
            await ShowErrorAsync("无法删除 Environment", result.Error!);
            return;
        }

        _selectedEnvironment = result.Value is Guid nextId
            ? _session.Project.Environments.FirstOrDefault(item => item.Id == nextId)
            : null;
        RefreshWorkspace();
    }

    private async void ProjectRoutes_Click(object sender, RoutedEventArgs e)
    {
        await EditProjectRoutePartsAsync();
    }

    private async Task EditProjectRoutePartsAsync()
    {
        if (_session.Project is null)
        {
            return;
        }

        var editor = new RoutePartsEditor();
        editor.SetItems(_session.Project.RouteParts);
        var dialog = CreateDialog("Project Route Parts", editor, "应用");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var parts = editor.GetItems();
        if (parts.Any(part => string.IsNullOrWhiteSpace(part.Name)))
        {
            await ShowErrorAsync("无法应用 Route Parts", "每个 Route Part 都需要一个来源名称。");
            return;
        }

        _session.Project.RouteParts.Clear();
        foreach (var part in parts)
        {
            _session.Project.RouteParts.Add(part);
        }

        RequestEditor.RefreshRoutePreview();
        _ = RefreshPreparedPreviewAsync();
        RefreshProjectChrome();
    }

    private async void EnvironmentVariables_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedEnvironment is not null)
        {
            await EditVariablesAsync(_selectedEnvironment.Variables, _selectedEnvironment.Name);
        }
    }

    private async void ProjectVariables_Click(object sender, RoutedEventArgs e)
    {
        if (_session.Project is not null)
        {
            await EditVariablesAsync(_session.Project.Variables, $"Project · {_session.Project.Name}");
        }
    }

    private async Task EditVariablesAsync(
        System.Collections.ObjectModel.ObservableCollection<VariableDefinition> variables,
        string scopeName)
    {
        if (_session.Project is null)
        {
            return;
        }

        try
        {
            var editor = new VariableEditor();
            await editor.LoadAsync(_session.Project.Id, variables, _secureValueStore);
            var dialog = CreateDialog($"Variables · {scopeName}", editor, "应用");
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            var result = await editor.ApplyAsync();
            if (!result.Succeeded || result.Value is null)
            {
                await ShowErrorAsync("无法应用变量", result.Error ?? "变量无效。");
                return;
            }

            variables.Clear();
            foreach (var variable in result.Value)
            {
                variables.Add(variable);
            }
            await RefreshPreparedPreviewAsync();
            if (_workspaceMode != WorkspaceMode.Request)
            {
                _ = RefreshRouteCatalogAsync();
            }
            RefreshProjectChrome();
        }
        catch (SecureValueStoreException exception)
        {
            await ShowErrorAsync("安全存储不可用", exception.Message);
        }
    }

    private async void InspectorPane_ManageVariablesRequested(object? sender, EventArgs e)
    {
        if (_session.Project is null)
        {
            return;
        }

        if (_selectedNode is not null)
        {
            await EditVariablesAsync(_selectedNode.Variables, _selectedNode.Name);
            return;
        }

        await EditVariablesAsync(_session.Project.Variables, _session.Project.Name);
    }

    private async void TokenSession_Click(object sender, RoutedEventArgs e)
    {
        if (_session.Project is null || _selectedEnvironment is null)
        {
            return;
        }

        var accessBox = new PasswordBox
        {
            Header = "Access Token",
            Password = _tokenSession?.AccessToken ?? string.Empty,
            PasswordRevealMode = PasswordRevealMode.Peek
        };
        var refreshBox = new PasswordBox
        {
            Header = "Refresh Token",
            Password = _tokenSession?.RefreshToken ?? string.Empty,
            PasswordRevealMode = PasswordRevealMode.Peek,
            Margin = new Thickness(0, 8, 0, 0)
        };
        var expiresBox = new TextBox
        {
            Header = "Expires at (ISO 8601, optional)",
            Text = _tokenSession?.ExpiresAt?.ToString("O") ?? string.Empty,
            Margin = new Thickness(0, 8, 0, 0)
        };
        var refreshRequests = ProjectOperations.EnumerateNodes(_session.Project.Items)
            .Where(node => node.Request is not null)
            .Select(node => new RequestChoice(node))
            .ToArray();
        var refreshRequestBox = new ComboBox
        {
            Header = "Refresh request",
            ItemsSource = refreshRequests,
            DisplayMemberPath = nameof(RequestChoice.Name),
            SelectedItem = refreshRequests.FirstOrDefault(choice =>
                choice.Node.Id == _session.Project.RefreshRequestNodeId),
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var panel = new StackPanel { MinWidth = 460 };
        panel.Children.Add(new TextBlock
        {
            Text = "Token 仅存于当前 Windows 用户的安全存储，并按 Project / Environment 隔离。",
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(accessBox);
        panel.Children.Add(refreshBox);
        panel.Children.Add(expiresBox);
        panel.Children.Add(refreshRequestBox);

        var dialog = CreateDialog(
            $"Token Session · {_selectedEnvironment.Name}",
            panel,
            "保存");
        dialog.SecondaryButtonText = "清除";
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Secondary)
        {
            try
            {
                await _tokenSessionService.ClearAsync(
                    _session.Project.Id,
                    _selectedEnvironment.Id);
            }
            catch (SecureValueStoreException exception)
            {
                DiagnosticsPane.ShowMessage("Token 清除失败", exception.Message, isError: true);
                return;
            }

            ReplaceTokenSession(null);
            DiagnosticsPane.ShowMessage("Token 已清除", "当前 Environment 的 Token 会话已清除。");
        }
        else if (result == ContentDialogResult.Primary)
        {
            DateTimeOffset? expiresAt = null;
            if (!string.IsNullOrWhiteSpace(expiresBox.Text))
            {
                if (!DateTimeOffset.TryParse(expiresBox.Text, out var parsedExpiry))
                {
                    await ShowErrorAsync("无法保存 Token", "Expires at 必须是有效的 ISO 8601 时间。");
                    return;
                }
                expiresAt = parsedExpiry;
            }

            var session = new TokenSession(
                accessBox.Password,
                refreshBox.Password,
                expiresAt,
                DateTimeOffset.Now);
            try
            {
                await _tokenSessionService.SaveAsync(
                    _session.Project.Id,
                    _selectedEnvironment.Id,
                    session);
            }
            catch (SecureValueStoreException exception)
            {
                DiagnosticsPane.ShowMessage("Token 保存失败", exception.Message, isError: true);
                return;
            }

            ReplaceTokenSession(session);
            _session.Project.RefreshRequestNodeId =
                (refreshRequestBox.SelectedItem as RequestChoice)?.Node.Id;
            DiagnosticsPane.ShowMessage("Token 已保存", "Token 会话和 Refresh 请求配置已更新。");
        }
        await RefreshPreparedPreviewAsync();
        RefreshCommandAvailability();
    }

    private async void RefreshToken_Click(object sender, RoutedEventArgs e) =>
        await RefreshTokenSessionAsync(showFeedback: true);

    private async Task<bool> RefreshTokenSessionAsync(bool showFeedback)
    {
        if (_session.Project is null || _selectedEnvironment is null)
        {
            return false;
        }

        var project = _session.Project;
        var environment = _selectedEnvironment;
        var currentSession = _tokenSession;
        TokenRefreshResult result;
        try
        {
            result = await _tokenSessionService.RefreshAsync(
                project,
                environment,
                currentSession,
                RequestEditor.RequestTimeout);
        }
        catch (SecureValueStoreException exception)
        {
            if (_session.Project == project &&
                _selectedEnvironment == environment &&
                showFeedback)
            {
                DiagnosticsPane.ShowMessage("安全存储不可用", exception.Message, isError: true);
            }
            return false;
        }

        if (_session.Project != project || _selectedEnvironment != environment)
        {
            return false;
        }
        if (!result.Succeeded || result.Session is null)
        {
            if (showFeedback)
            {
                DiagnosticsPane.ShowMessage(
                    "Token 刷新失败",
                    result.Error ?? "Refresh 请求失败，原 Token 会话已保留。",
                    isError: true);
            }
            return false;
        }

        ReplaceTokenSession(result.Session);
        if (showFeedback)
        {
            DiagnosticsPane.ShowMessage("Token 已刷新", "新的 Token 已保存到当前 Environment 安全会话。");
        }
        await RefreshPreparedPreviewAsync();
        return true;
    }

    private async Task LoadTokenAndPreviewAsync()
    {
        var version = ++_tokenContextVersion;
        var project = _session.Project;
        var environment = _selectedEnvironment;
        try
        {
            var loadedSession = project is null || environment is null
                ? null
                : await _tokenSessionService.LoadAsync(project.Id, environment.Id);
            if (version != _tokenContextVersion ||
                project != _session.Project ||
                environment != _selectedEnvironment)
            {
                return;
            }
            _tokenSession = loadedSession;
            await RefreshPreparedPreviewAsync();
            if (_workspaceMode != WorkspaceMode.Request)
            {
                _ = RefreshRouteCatalogAsync(RouteCatalogRefreshMode.Immediate);
            }
        }
        catch (SecureValueStoreException exception)
        {
            if (version != _tokenContextVersion ||
                project != _session.Project ||
                environment != _selectedEnvironment)
            {
                return;
            }

            _tokenSession = null;
            if (_workspaceMode != WorkspaceMode.Request)
            {
                _ = RefreshRouteCatalogAsync(RouteCatalogRefreshMode.Immediate);
            }
            DiagnosticsPane.ShowMessage("安全存储不可用", exception.Message, isError: true);
        }
    }

    private void ReplaceTokenSession(TokenSession? session)
    {
        _tokenContextVersion++;
        _tokenSession = session;
        if (_workspaceMode != WorkspaceMode.Request)
        {
            _ = RefreshRouteCatalogAsync(RouteCatalogRefreshMode.Immediate);
        }
    }

    private async Task RefreshPreparedPreviewAsync()
    {
        var version = ++_previewVersion;
        if (_session.Project is null || RequestEditor.CurrentNode is not { Request: not null } node)
        {
            InspectorPane.ShowSnapshot(null);
            return;
        }

        var executionProject = _session.Project;
        var executionEnvironment = _selectedEnvironment;
        var executionTokenSession = _tokenSession;
        PreparedRequestResult prepared;
        try
        {
            prepared = await RequestPreparationService.PrepareAsync(
                executionProject,
                executionEnvironment,
                node,
                RequestEditor.RequestTimeout,
                _secureValueStore,
                executionTokenSession);
        }
        catch (SecureValueStoreException exception)
        {
            if (version == _previewVersion)
            {
                DiagnosticsPane.ShowMessage("安全存储不可用", exception.Message, isError: true);
            }
            return;
        }
        if (version == _previewVersion && node.Id == RequestEditor.CurrentNode?.Id)
        {
            ShowPreparedResult(
                executionProject,
                executionEnvironment,
                node,
                prepared,
                executionTokenSession,
                showValidation: false);
        }
    }

    private void ShowPreparedResult(
        ProjectDocument project,
        ProjectEnvironment? environment,
        ProjectNode node,
        PreparedRequestResult prepared,
        TokenSession? tokenSession,
        bool showValidation)
    {
        RequestEditor.ShowPrepared(prepared, showValidation);
        InspectorPane.ShowSnapshot(RequestInspectorSnapshotFactory.Create(
            project,
            environment,
            node,
            prepared,
            tokenSession,
            DateTimeOffset.UtcNow));
    }

    private void WorkspacePane_NodeInvoked(object? sender, WorkspaceNodeEventArgs e)
    {
        _diagnosticCancellation?.Cancel();
        _selectedNode = e.Node;
        _diagnosticResults.Select(_selectedNode?.Request is null ? null : _selectedNode.Id);
        if (_session.Project is not null)
        {
            _session.Project.SelectedNodeId = _selectedNode?.Id;
        }

        RequestEditor.ShowRequest(_session.Project, _selectedNode, _selectedEnvironment);
        if (_selectedNode?.Request is null)
        {
            InspectorPane.ShowSnapshot(null);
        }
        InspectorPane.ShowDiagnostics(_diagnosticResults.Current());
        RequestEditor.SetExecutionState(_requestCancellation is not null);
        RefreshDisplayedResponse();
        RefreshCommandAvailability();
        _ = RefreshPreparedPreviewAsync();
    }

    private async void WorkspacePane_CommandRequested(object? sender, WorkspaceCommandEventArgs e)
    {
        if (_session.Project is null && e.Command != WorkspaceCommand.DeleteProject)
        {
            await ShowErrorAsync("没有打开的项目", "请先新建或打开一个本地项目。");
            return;
        }

        switch (e.Command)
        {
            case WorkspaceCommand.AddRootGroup:
                await AddGroupAsync(null);
                break;
            case WorkspaceCommand.AddNestedGroup:
                await AddGroupAsync(e.Node);
                break;
            case WorkspaceCommand.AddEndpoint:
                await AddEndpointAsync(e.Node);
                break;
            case WorkspaceCommand.AddRequestCase:
                await AddRequestCaseAsync(e.Node);
                break;
            case WorkspaceCommand.EditGroupRoute:
                await EditGroupRouteAsync(e.Node);
                break;
            case WorkspaceCommand.EditVariables:
                var project = _session.Project!;
                await EditVariablesAsync(
                    e.Node?.Variables ?? project.Variables,
                    e.Node?.Name ?? project.Name);
                break;
            case WorkspaceCommand.RenameNode:
                await RenameNodeAsync(e.Node);
                break;
            case WorkspaceCommand.DeleteNode:
                await DeleteNodeAsync(e.Node);
                break;
            case WorkspaceCommand.RenameProject:
                await RenameProjectAsync();
                break;
            case WorkspaceCommand.DeleteProject:
                await DeleteProjectAsync();
                break;
        }
    }

    private async void NewEndpoint_Click(object sender, RoutedEventArgs e)
    {
        await AddEndpointAsync(GetSelectedGroup());
    }

    private async void NewRequestCase_Click(object sender, RoutedEventArgs e)
    {
        await AddRequestCaseAsync(GetSelectedEndpoint());
    }

    private async Task AddGroupAsync(ProjectNode? parent)
    {
        if (_session.Project is null)
        {
            return;
        }

        var name = await PromptForNameAsync(
            parent is null ? "新建根分组" : "新建子分组",
            "分组名称",
            "例如：用户与身份");
        if (name is null)
        {
            return;
        }

        var result = ProjectOperations.AddGroup(_session.Project, parent?.Id, name);
        await HandleCreatedNodeAsync(result, focusRoute: false);
    }

    private async Task AddEndpointAsync(ProjectNode? group)
    {
        if (_session.Project is null)
        {
            return;
        }

        if (group?.Kind != ProjectNodeKind.Group)
        {
            await ShowErrorAsync("无法新建 Endpoint", "请先选择一个分组。");
            return;
        }

        var name = await PromptForNameAsync("新建 Endpoint", "Endpoint 名称", "例如：创建用户");
        if (name is null)
        {
            return;
        }

        var result = ProjectOperations.AddEndpoint(_session.Project, group.Id, name);
        await HandleCreatedNodeAsync(result, focusRoute: true);
    }

    private async Task EditGroupRouteAsync(ProjectNode? group)
    {
        if (group?.Kind != ProjectNodeKind.Group)
        {
            return;
        }

        var enabledBox = new CheckBox
        {
            Content = "此 Group 为子级请求贡献 Route Prefix",
            IsChecked = group.IsRoutePrefixEnabled
        };
        var prefixBox = new TextBox
        {
            Header = "Route Prefix",
            PlaceholderText = "/auth",
            Text = group.RoutePrefix,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
            MinWidth = 400,
            Margin = new Thickness(0, 8, 0, 0)
        };
        VariableDropBehavior.SetIsEnabled(prefixBox, true);
        var panel = new StackPanel();
        panel.Children.Add(enabledBox);
        panel.Children.Add(prefixBox);
        var dialog = CreateDialog($"编辑“{group.Name}”的 Route Prefix", panel, "应用");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        group.RoutePrefix = prefixBox.Text.Trim();
        group.IsRoutePrefixEnabled = enabledBox.IsChecked == true;
        RequestEditor.RefreshRoutePreview();
        _ = RefreshPreparedPreviewAsync();
        RefreshProjectChrome();
    }

    private async Task AddRequestCaseAsync(ProjectNode? endpoint)
    {
        if (_session.Project is null)
        {
            return;
        }

        if (endpoint?.Kind != ProjectNodeKind.Endpoint)
        {
            await ShowErrorAsync("无法新建 Request Case", "请先选择一个 Endpoint 或其 Request Case。");
            return;
        }

        var name = await PromptForNameAsync("新建 Request Case", "Case 名称", "例如：无效邮箱");
        if (name is null)
        {
            return;
        }

        var source = _selectedNode?.Request ?? endpoint.Request;
        var result = ProjectOperations.AddRequestCase(_session.Project, endpoint.Id, name, source);
        await HandleCreatedNodeAsync(result, focusRoute: false);
    }

    private async Task HandleCreatedNodeAsync(OperationResult<ProjectNode> result, bool focusRoute)
    {
        if (!result.Succeeded || result.Value is null)
        {
            await ShowErrorAsync("无法创建对象", result.Error ?? "操作失败。");
            return;
        }

        _selectedNode = result.Value;
        RefreshWorkspace();
        WorkspacePane.SelectNode(result.Value.Id);
        if (focusRoute)
        {
            RequestEditor.FocusRouteEditor();
        }
    }

    private async Task RenameNodeAsync(ProjectNode? node)
    {
        if (_session.Project is null || node is null)
        {
            return;
        }

        var name = await PromptForNameAsync("重命名", "名称", node.Name, node.Name);
        if (name is null)
        {
            return;
        }

        var result = ProjectOperations.RenameNode(_session.Project, node.Id, name);
        if (!result.Succeeded)
        {
            await ShowErrorAsync("无法重命名", result.Error!);
            return;
        }

        RefreshWorkspace();
        WorkspacePane.SelectNode(node.Id);
    }

    private async Task DeleteNodeAsync(ProjectNode? node)
    {
        if (_session.Project is null || node is null)
        {
            return;
        }

        var contains = node.Kind switch
        {
            ProjectNodeKind.Group => "其中嵌套的分组、Endpoint 和 Request Case 也会被删除。",
            ProjectNodeKind.Endpoint => "其中的所有 Request Case 也会被删除。",
            _ => "该 Request Case 的请求内容会被删除。"
        };
        if (!await ConfirmDeleteAsync("删除工作区对象", $"确定删除“{node.Name}”吗？{contains}"))
        {
            return;
        }

        var result = ProjectOperations.DeleteNode(_session.Project, node.Id);
        if (!result.Succeeded)
        {
            await ShowErrorAsync("无法删除", result.Error!);
            return;
        }

        _selectedNode = result.Value?.SuggestedSelectionId is Guid selectedId
            ? ProjectOperations.FindNode(_session.Project, selectedId)
            : null;
        RefreshWorkspace();
    }

    private async Task RenameProjectAsync()
    {
        if (_session.Project is null)
        {
            return;
        }

        var name = await PromptForNameAsync(
            "重命名项目",
            "项目名称",
            _session.Project.Name,
            _session.Project.Name);
        if (name is null)
        {
            return;
        }

        var result = ProjectOperations.RenameProject(_session.Project, name);
        if (!result.Succeeded)
        {
            await ShowErrorAsync("无法重命名项目", result.Error!);
            return;
        }

        RefreshProjectChrome();
    }

    private async Task DeleteProjectAsync()
    {
        if (_session.Project is null)
        {
            return;
        }

        var savedPath = _session.ProjectFilePath;
        var message = string.IsNullOrWhiteSpace(savedPath)
            ? $"确定删除未保存的项目“{_session.Project.Name}”吗？"
            : $"确定删除项目“{_session.Project.Name}”及其本地文件吗？\r\n\r\n{savedPath}";
        if (!await ConfirmDeleteAsync("删除项目", message))
        {
            return;
        }

        try
        {
            var result = await _session.DeleteCurrentProjectAsync();
            ApplyProjectTransition(result.Transition);
            if (result.Warning is not null)
            {
                DiagnosticsPane.ShowMessage("项目已删除", result.Warning, isError: true);
            }
        }
        catch (ProjectFileException exception)
        {
            await ShowErrorAsync("无法删除项目", exception.Message);
        }
    }

    private ProjectNode? GetSelectedGroup()
    {
        if (_session.Project is null || _selectedNode is null)
        {
            return null;
        }

        if (_selectedNode.Kind == ProjectNodeKind.Group)
        {
            return _selectedNode;
        }

        var parent = ProjectOperations.FindParent(_session.Project, _selectedNode.Id);
        while (parent is not null && parent.Kind != ProjectNodeKind.Group)
        {
            parent = ProjectOperations.FindParent(_session.Project, parent.Id);
        }

        return parent;
    }

    private ProjectNode? GetSelectedEndpoint()
    {
        if (_session.Project is null || _selectedNode is null)
        {
            return null;
        }

        return _selectedNode.Kind switch
        {
            ProjectNodeKind.Endpoint => _selectedNode,
            ProjectNodeKind.RequestCase => ProjectOperations.FindParent(_session.Project, _selectedNode.Id),
            _ => null
        };
    }

    private void RequestEditor_RequestChanged(object? sender, EventArgs e)
    {
        RefreshProjectChrome();
        _ = RefreshPreparedPreviewAsync();
        if (_workspaceMode != WorkspaceMode.Request)
        {
            _ = RefreshRouteCatalogAsync();
        }
    }

    private void RequestWorkspace_Click(object sender, RoutedEventArgs e) =>
        SetWorkspaceMode(WorkspaceMode.Request);

    private void RouteMap_Click(object sender, RoutedEventArgs e)
    {
        SetWorkspaceMode(WorkspaceMode.RouteMap);
        _ = RefreshRouteCatalogAsync(RouteCatalogRefreshMode.Immediate);
    }

    private void Documentation_Click(object sender, RoutedEventArgs e)
    {
        SetWorkspaceMode(WorkspaceMode.Documentation);
        _ = RefreshRouteCatalogAsync(RouteCatalogRefreshMode.Immediate);
    }

    private void SetWorkspaceMode(WorkspaceMode mode)
    {
        if (_workspaceMode == WorkspaceMode.Request && DiagnosticsRow.ActualHeight > 0)
        {
            _requestDiagnosticsHeight = new GridLength(DiagnosticsRow.ActualHeight);
        }
        _workspaceMode = mode;
        RequestWorkspaceGrid.Visibility = mode == WorkspaceMode.Request ? Visibility.Visible : Visibility.Collapsed;
        RouteMapPane.Visibility = mode == WorkspaceMode.RouteMap ? Visibility.Visible : Visibility.Collapsed;
        DocumentationPane.Visibility = mode == WorkspaceMode.Documentation ? Visibility.Visible : Visibility.Collapsed;
        InspectorSplitter.Visibility = mode == WorkspaceMode.Request ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsPane.Visibility = mode == WorkspaceMode.Request ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsSplitter.Visibility = mode == WorkspaceMode.Request ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsRow.MinHeight = mode == WorkspaceMode.Request ? 170 : 0;
        DiagnosticsRow.Height = mode == WorkspaceMode.Request ? _requestDiagnosticsHeight : new GridLength(0);
        RequestWorkspaceButton.IsChecked = mode == WorkspaceMode.Request;
        RouteMapButton.IsChecked = mode == WorkspaceMode.RouteMap;
        DocumentationButton.IsChecked = mode == WorkspaceMode.Documentation;
    }

    private async Task RefreshRouteCatalogAsync(
        RouteCatalogRefreshMode mode = RouteCatalogRefreshMode.Debounced)
    {
        var project = _session.Project;
        try
        {
            var result = await _routeCatalogService.RefreshAsync(
                project,
                _selectedEnvironment,
                _tokenSession,
                mode);
            if (result is null ||
                !_routeCatalogService.IsCurrent(result) ||
                !ReferenceEquals(result.Project, _session.Project))
            {
                return;
            }

            RouteMapPane.ShowCatalog(result.Catalog);
            DocumentationPane.ShowCatalog(result.Project, result.Catalog, _selectedNode?.Id);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            DiagnosticsPane.ShowMessage("Route Catalog 无法生成", exception.Message, isError: true);
        }
    }

    private void RouteMapPane_EntryInvoked(object? sender, RouteCatalogEntryInvokedEventArgs e)
    {
        if (_session.Project is null ||
            ProjectOperations.FindNode(_session.Project, e.NodeId) is not { } node)
        {
            return;
        }
        _selectedNode = node;
        _session.Project.SelectedNodeId = node.Id;
        WorkspacePane.SelectNode(node.Id);
        SetWorkspaceMode(WorkspaceMode.Request);
        RequestEditor.ShowRequest(_session.Project, node, _selectedEnvironment);
        _ = RefreshPreparedPreviewAsync();
    }

    private void RouteMapPane_RefreshRequested(object? sender, EventArgs e) =>
        _ = RefreshRouteCatalogAsync(RouteCatalogRefreshMode.Immediate);

    private void DocumentationPane_MetadataChanged(object? sender, EventArgs e)
    {
        RefreshProjectChrome();
        _ = RefreshRouteCatalogAsync();
    }

    private async void DocumentationPane_ExportRequested(
        object? sender,
        MarkdownExportRequestedEventArgs e)
    {
        try
        {
            var picker = new FileSavePicker { SuggestedFileName = SanitizeFileName(e.SuggestedName) };
            picker.FileTypeChoices.Add("Markdown", [".md"]);
            InitializePicker(picker);
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            await Windows.Storage.FileIO.WriteTextAsync(file, e.Markdown);
            DiagnosticsPane.ShowMessage("文档已导出", file.Path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await ShowErrorAsync("无法导出文档", exception.Message);
        }
    }

    private void InspectorPane_RoutePartMoveRequested(
        object? sender,
        RoutePartMoveRequestedEventArgs e)
    {
        if (_session.Project is null || _selectedNode?.Request is null)
        {
            return;
        }

        var moved = false;
        if (e.Source.Kind == RouteCompositionPartKind.ProjectRoutePart &&
            e.Source.SourceId is Guid sourceId)
        {
            var sourceIndex = _session.Project.RouteParts.ToList()
                .FindIndex(part => part.Id == sourceId);
            var targetIndex = e.KeyboardDelta == 0
                ? _session.Project.RouteParts.ToList()
                    .FindIndex(part => part.Id == e.Target.SourceId)
                : sourceIndex + e.KeyboardDelta;
            if (e.KeyboardDelta == 0 && e.InsertAfter)
            {
                targetIndex++;
            }
            if (e.KeyboardDelta == 0 && sourceIndex >= 0 && sourceIndex < targetIndex)
            {
                targetIndex--;
            }
            moved = RouteReorderService.MoveProjectRoutePart(
                _session.Project, sourceId, targetIndex);
        }
        else if (e.Source.Kind == RouteCompositionPartKind.QueryParameter &&
                 e.Source.FieldIndex is int sourceIndex)
        {
            var targetIndex = e.KeyboardDelta == 0
                ? e.Target.FieldIndex ?? sourceIndex
                : sourceIndex + e.KeyboardDelta;
            if (e.KeyboardDelta == 0 && e.InsertAfter)
            {
                targetIndex++;
            }
            if (e.KeyboardDelta == 0 && sourceIndex < targetIndex)
            {
                targetIndex--;
            }
            moved = RouteReorderService.MoveQueryParameter(
                _selectedNode.Request, sourceIndex, targetIndex);
        }

        if (moved)
        {
            RequestEditor.RefreshRoutePreview();
            RefreshProjectChrome();
            _ = RefreshPreparedPreviewAsync();
        }
    }

    private async void RequestEditor_ValidateRequested(object? sender, EventArgs e)
    {
        await ValidateCurrentRequestAsync();
    }

    private async void RequestEditor_SendRequested(object? sender, EventArgs e)
    {
        await SendCurrentRequestAsync();
    }

    private async void InspectorPane_DiagnosticsRequested(object? sender, EventArgs e)
    {
        await DiagnoseCurrentRequestAsync();
    }

    private void InspectorPane_DiagnosticsCancelRequested(object? sender, EventArgs e) =>
        _diagnosticCancellation?.Cancel();

    private void RequestEditor_CancelRequested(object? sender, EventArgs e) =>
        _requestCancellation?.Cancel();

    private void DiagnosticsPane_ClearHistoryRequested(object? sender, EventArgs e) =>
        _requestHistory.Clear();

    private void RequestEditor_FeedbackRequested(object? sender, FeedbackEventArgs e)
    {
        DiagnosticsPane.ShowMessage("完成", e.Message);
    }

    private async Task ValidateCurrentRequestAsync()
    {
        if (_session.Project is null || RequestEditor.CurrentNode is not { Request: not null } node)
        {
            DiagnosticsPane.ShowMessage("没有请求", "请先选择一个 Endpoint 或 Request Case。", isError: true);
            return;
        }

        var executionProject = _session.Project;
        var executionEnvironment = _selectedEnvironment;
        var executionTokenSession = _tokenSession;
        PreparedRequestResult prepared;
        try
        {
            prepared = await RequestPreparationService.PrepareAsync(
                executionProject,
                executionEnvironment,
                node,
                RequestEditor.RequestTimeout,
                _secureValueStore,
                executionTokenSession);
        }
        catch (SecureValueStoreException exception)
        {
            DiagnosticsPane.ShowMessage("安全存储不可用", exception.Message, isError: true);
            return;
        }
        ShowPreparedResult(
            executionProject,
            executionEnvironment,
            node,
            prepared,
            executionTokenSession,
            showValidation: true);
        DiagnosticsPane.ShowValidation(prepared.Validation, _selectedNode?.Name ?? "当前请求");
        ValidateEnvironmentInline();
    }

    private async Task SendCurrentRequestAsync()
    {
        if (_requestCancellation is not null)
        {
            return;
        }

        if (_session.Project is null || RequestEditor.CurrentNode is not { Request: not null } node)
        {
            DiagnosticsPane.ShowMessage("无法发送", "请先选择一个 Endpoint 或 Request Case。", isError: true);
            return;
        }

        var executionProject = _session.Project;
        var executionEnvironment = _selectedEnvironment;
        RequestExecutionPreparation preparation;
        try
        {
            preparation = await _requestExecutionService.PrepareAsync(
                executionProject,
                executionEnvironment,
                node,
                RequestEditor.RequestTimeout,
                _tokenSession);
        }
        catch (SecureValueStoreException exception)
        {
            DiagnosticsPane.ShowMessage("安全存储不可用", exception.Message, isError: true);
            return;
        }

        if (preparation.TokenSessionChanged &&
            _session.Project == executionProject &&
            _selectedEnvironment == executionEnvironment)
        {
            ReplaceTokenSession(preparation.TokenSession);
        }

        ShowPreparedResult(
            executionProject,
            executionEnvironment,
            node,
            preparation.Prepared,
            preparation.TokenSession,
            showValidation: true);
        if (!preparation.Prepared.Succeeded || preparation.Prepared.Plan is null)
        {
            DiagnosticsPane.ShowValidation(preparation.Prepared.Validation, node.Name);
            ValidateEnvironmentInline();
            return;
        }

        var cancellation = new CancellationTokenSource();
        _requestCancellation = cancellation;
        _activeRequestNodeId = node.Id;
        _activeRequestName = node.Name;
        _activeRequestUrl = preparation.Prepared.Plan.DisplayUrl;
        RequestEditor.SetExecutionState(true);
        DiagnosticsPane.ShowRunning(_activeRequestName, _activeRequestUrl);

        RequestExecutionOutcome outcome;
        try
        {
            outcome = await _requestExecutionService.ExecuteAsync(
                executionProject,
                executionEnvironment,
                node,
                preparation,
                cancellation.Token);
        }
        finally
        {
            if (ReferenceEquals(_requestCancellation, cancellation))
            {
                _requestCancellation = null;
                _activeRequestNodeId = null;
                _activeRequestName = null;
                _activeRequestUrl = null;
            }

            cancellation.Dispose();
            RequestEditor.SetExecutionState(_requestCancellation is not null);
        }

        if (outcome.TokenSessionChanged &&
            _session.Project == executionProject &&
            _selectedEnvironment == executionEnvironment)
        {
            ReplaceTokenSession(outcome.TokenSession);
        }

        if (outcome.TokenCapture is { } capture)
        {
            if (capture.Succeeded && capture.Session is not null)
            {
                if (outcome.TokenCapturePersistenceError is null)
                {
                    DiagnosticsPane.ShowMessage(
                        "Token 已保存",
                        "响应中的 Token 已保存到当前 Environment 安全会话。");
                }
                else
                {
                    DiagnosticsPane.ShowMessage(
                        "Token 保存失败",
                        outcome.TokenCapturePersistenceError,
                        isError: true);
                }
            }
            else
            {
                DiagnosticsPane.ShowMessage(
                    "Token 提取失败",
                    capture.Error ?? "无法提取 Token。",
                    isError: true);
            }

            if (_session.Project == executionProject &&
                _selectedEnvironment == executionEnvironment)
            {
                _ = RefreshPreparedPreviewAsync();
            }
        }

        if (_selectedNode?.Id == outcome.Execution.RequestNodeId)
        {
            DiagnosticsPane.ShowExecution(outcome.Execution);
        }
    }

    private void CancelActiveRequest()
    {
        _requestCancellation?.Cancel();
        _diagnosticCancellation?.Cancel();
    }

    private async Task DiagnoseCurrentRequestAsync()
    {
        if (_diagnosticCancellation is not null)
        {
            return;
        }
        if (_session.Project is null || RequestEditor.CurrentNode is not { Request: not null } node)
        {
            InspectorPane.SelectPage(InspectorPage.Diagnostics);
            InspectorPane.ShowDiagnostics(null);
            return;
        }

        var project = _session.Project;
        var environment = _selectedEnvironment;
        PreparedRequestResult prepared;
        try
        {
            prepared = await RequestPreparationService.PrepareAsync(
                project,
                environment,
                node,
                RequestEditor.RequestTimeout,
                _secureValueStore,
                _tokenSession);
        }
        catch (SecureValueStoreException exception)
        {
            DiagnosticsPane.ShowMessage("安全存储不可用", exception.Message, isError: true);
            return;
        }

        ShowPreparedResult(project, environment, node, prepared, _tokenSession, showValidation: true);
        InspectorPane.SelectPage(InspectorPage.Diagnostics);
        if (!prepared.Succeeded || prepared.Plan is null)
        {
            InspectorPane.ShowDiagnosticValidation(prepared.Validation);
            DiagnosticsPane.ShowValidation(prepared.Validation, node.Name);
            return;
        }

        var cancellation = new CancellationTokenSource();
        _diagnosticCancellation = cancellation;
        InspectorPane.SetDiagnosticsRunning(true);
        try
        {
            var result = await _networkDiagnostics.DiagnoseAsync(
                prepared.Plan,
                prepared.RequiresTokenRefresh,
                cancellation.Token,
                executeHttp: prepared.Plan.Method is "GET" or "HEAD" or "OPTIONS" ||
                             InspectorPane.IncludeUnsafeHttpRequest);
            if (!ReferenceEquals(project, _session.Project))
            {
                return;
            }

            var isCurrent = _diagnosticResults.TryStore(result);
            if (isCurrent && _selectedNode?.Id == result.RequestNodeId)
            {
                InspectorPane.ShowDiagnostics(result);
            }
        }
        catch (Exception exception)
        {
            InspectorPane.ShowDiagnosticError(
                "诊断无法完成",
                exception.Message);
            DiagnosticsPane.ShowMessage("诊断无法完成", exception.Message, isError: true);
        }
        finally
        {
            if (ReferenceEquals(_diagnosticCancellation, cancellation))
            {
                _diagnosticCancellation = null;
                InspectorPane.SetDiagnosticsRunning(false);
            }
            cancellation.Dispose();
        }
    }

    private void RefreshDisplayedResponse()
    {
        Guid? selectedRequestId = _selectedNode?.Request is null ? null : _selectedNode.Id;
        if (selectedRequestId is not null &&
            selectedRequestId == _activeRequestNodeId &&
            _activeRequestName is not null &&
            _activeRequestUrl is not null)
        {
            DiagnosticsPane.ShowRunning(_activeRequestName, _activeRequestUrl);
            return;
        }

        DiagnosticsPane.ShowForRequest(selectedRequestId);
    }

    private async Task<bool> ConfirmUnsavedChangesAsync(string action)
    {
        if (_session.Project?.IsDirty != true)
        {
            return true;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "项目有未保存的修改",
            Content = $"在{action}之前，是否保存“{_session.Project.Name}”？",
            PrimaryButtonText = "保存",
            SecondaryButtonText = "不保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        var result = await dialog.ShowAsync();
        return result switch
        {
            ContentDialogResult.Primary => await SaveProjectAsync(forceChoosePath: false),
            ContentDialogResult.Secondary => true,
            _ => false
        };
    }

    private async Task<string?> PromptForNameAsync(
        string title,
        string label,
        string placeholder,
        string? initialValue = null)
    {
        var textBox = new TextBox
        {
            Header = label,
            PlaceholderText = placeholder,
            Text = initialValue ?? string.Empty,
            SelectionStart = 0,
            SelectionLength = initialValue?.Length ?? 0,
            MinWidth = 360
        };
        var dialog = CreateDialog(title, textBox, "确定");
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(textBox.Text)
            ? textBox.Text.Trim()
            : null;
    }

    private async Task<(string Name, string BaseUrl)?> PromptForEnvironmentAsync(
        string title,
        ProjectEnvironment? environment)
    {
        var nameBox = new TextBox
        {
            Header = "Environment 名称",
            PlaceholderText = "例如：开发环境",
            Text = environment?.Name ?? string.Empty,
            MinWidth = 380,
            Style = (Style)Application.Current.Resources["ProbeLoomCompactTextBoxStyle"]
        };
        var baseUrlBox = new TextBox
        {
            Header = "Base URL",
            PlaceholderText = "https://api.example.com",
            Text = environment?.BaseUrl ?? string.Empty,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
            Style = (Style)Application.Current.Resources["ProbeLoomCompactTextBoxStyle"]
        };
        VariableDropBehavior.SetIsEnabled(baseUrlBox, true);
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(nameBox);
        panel.Children.Add(baseUrlBox);
        var dialog = CreateDialog(title, panel, "确定");
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(nameBox.Text))
        {
            return null;
        }

        return (nameBox.Text.Trim(), baseUrlBox.Text.Trim());
    }

    private async Task<bool> ConfirmDeleteAsync(string title, string message)
    {
        var dialog = CreateDialog(
            title,
            new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 },
            "删除");
        dialog.DefaultButton = ContentDialogButton.Close;
        dialog.PrimaryButtonStyle = (Style)Application.Current.Resources["ProbeLoomDangerButtonStyle"];
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task ShowErrorAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 480 },
            CloseButtonText = "确定",
            DefaultButton = ContentDialogButton.Close
        };
        await dialog.ShowAsync();
    }

    private ContentDialog CreateDialog(string title, object content, string primaryButtonText) =>
        new()
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = content,
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };

    private void Page_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.F5)
        {
            _ = ValidateCurrentRequestAsync();
            e.Handled = true;
        }
    }

    private void NavigationSplitter_DragDelta(object sender, DragDeltaEventArgs e)
    {
        NavigationColumn.Width = new GridLength(
            Math.Clamp(NavigationColumn.ActualWidth + e.HorizontalChange, 224, 460),
            GridUnitType.Pixel);
        ApplyInspectorLayout();
    }

    private void Page_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyInspectorLayout();

    private void InspectorPane_ExpandRequested(object? sender, EventArgs e)
    {
        _inspectorLayout = _inspectorLayout with { IsExpanded = true };
        _inspectorPreferences.Save(_inspectorLayout);
        ApplyInspectorLayout();
    }

    private void InspectorPane_CollapseRequested(object? sender, EventArgs e)
    {
        _inspectorLayout = _inspectorLayout with { IsExpanded = false };
        _inspectorPreferences.Save(_inspectorLayout);
        ApplyInspectorLayout();
    }

    private void InspectorSplitter_DragDelta(object sender, DragDeltaEventArgs e)
    {
        _inspectorLayout = _inspectorLayout.Resize(
            e.HorizontalChange,
            RequestWorkspaceGrid.ActualWidth);
        _inspectorPreferences.Save(_inspectorLayout);
        ApplyInspectorLayout();
    }

    private void ApplyInspectorLayout()
    {
        if (InspectorPane is null)
        {
            return;
        }

        var decision = _inspectorLayout.Decide(RequestWorkspaceGrid.ActualWidth);
        InspectorColumn.Width = new GridLength(decision.Width, GridUnitType.Pixel);
        InspectorSplitterColumn.Width = new GridLength(decision.IsExpanded ? 5 : 0);
        InspectorSplitter.Visibility = decision.IsExpanded ? Visibility.Visible : Visibility.Collapsed;
        InspectorPane.SetLayout(decision.IsExpanded, decision.CanExpand);
    }

    private async void InspectorPane_EditRequested(
        object? sender,
        InspectorEditRequestedEventArgs e)
    {
        switch (e.Part.EditTarget)
        {
            case InspectorEditTarget.Environment:
                BaseUrlTextBox.Focus(FocusState.Programmatic);
                BaseUrlTextBox.SelectAll();
                break;
            case InspectorEditTarget.ProjectRouteParts:
                await EditProjectRoutePartsAsync();
                break;
            case InspectorEditTarget.GroupPrefix:
                if (_session.Project is not null &&
                    e.Part.SourceId is Guid groupId &&
                    ProjectOperations.FindNode(_session.Project, groupId) is { } group)
                {
                    await EditGroupRouteAsync(group);
                }
                break;
            case InspectorEditTarget.EndpointRoute:
                RequestEditor.FocusRouteEditor();
                break;
            case InspectorEditTarget.PathParameters:
                RequestEditor.SelectSection(RequestEditorSection.Path);
                break;
            case InspectorEditTarget.QueryParameters:
                RequestEditor.SelectSection(RequestEditorSection.Params);
                break;
        }
    }

    private void DiagnosticsSplitter_DragDelta(object sender, DragDeltaEventArgs e)
    {
        DiagnosticsRow.Height = new GridLength(
            Math.Clamp(DiagnosticsRow.ActualHeight - e.VerticalChange, 118, 390),
            GridUnitType.Pixel);
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        return string.Concat(fileName.Select(character => invalidCharacters.Contains(character) ? '_' : character));
    }

    private static void InitializePicker(object picker)
    {
        var window = (Application.Current as App)?.MainWindowInstance
                     ?? throw new InvalidOperationException("应用窗口尚未初始化。");
        var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);
    }

    private sealed class RequestChoice(ProjectNode node)
    {
        public ProjectNode Node { get; } = node;

        public string Name { get; } = $"{node.Kind} · {node.Name}";
    }

    private enum WorkspaceMode
    {
        Request,
        RouteMap,
        Documentation
    }
}
