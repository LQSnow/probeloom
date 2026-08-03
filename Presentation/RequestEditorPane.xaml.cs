using System.Collections.ObjectModel;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using ProbeLoom.Core;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace ProbeLoom.Presentation;

public sealed class FeedbackEventArgs(string message) : EventArgs
{
    public string Message { get; } = message;
}

public enum RequestEditorSection
{
    Params,
    Path,
    Headers,
    Body,
    Auth
}

public sealed partial class RequestEditorPane : UserControl
{
    private static readonly string[] Methods = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"];
    private readonly TextEditHistory _bodyHistory = new();
    private ProjectDocument? _project;
    private ProjectNode? _node;
    private bool _isLoading;
    private bool _suppressBodyHistory;
    private bool _suppressNextBodyNewLineCharacter;
    private TextEditorState _lastBodyState = new(string.Empty, 0, 0);

    public RequestEditorPane()
    {
        InitializeComponent();
        MethodComboBox.ItemsSource = Methods;
        AuthenticationKindComboBox.ItemsSource = Enum.GetValues<AuthenticationKind>();
        ApiKeyLocationComboBox.ItemsSource = Enum.GetValues<ApiKeyLocation>();
    }

    public event EventHandler? RequestChanged;

    public event EventHandler? ValidateRequested;

    public event EventHandler? SendRequested;

    public event EventHandler? CancelRequested;

    public event EventHandler<FeedbackEventArgs>? FeedbackRequested;

    public RequestDefinition? CurrentRequest => _node?.Request;

    public ProjectNode? CurrentNode => _node;

    public TimeSpan RequestTimeout => TimeSpan.FromSeconds(
        double.IsFinite(TimeoutNumberBox.Value) ? TimeoutNumberBox.Value : 30);

    public void ShowRequest(ProjectDocument? project, ProjectNode? node, ProjectEnvironment? environment)
    {
        _project = project;
        _node = node?.Request is null ? null : node;
        _isLoading = true;

        if (_node?.Request is null)
        {
            EditorContent.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Visible;
            _bodyHistory.Reset();
            _lastBodyState = new TextEditorState(string.Empty, 0, 0);
            _isLoading = false;
            return;
        }

        EditorContent.Visibility = Visibility.Visible;
        EmptyState.Visibility = Visibility.Collapsed;
        RequestNameText.Text = _node.Name;
        RequestKindText.Text = _node.Kind == ProjectNodeKind.RequestCase ? "CASE" : "ENDPOINT";
        RequestContextText.Text = _node.Kind == ProjectNodeKind.RequestCase
            ? "当前 Endpoint 的独立 Request Case"
            : "Endpoint 默认请求";
        MethodComboBox.SelectedItem = _node.Request.Method;
        RefreshRoutePreview();
        RouteTextBox.Text = _node.Request.Route;
        BodyTextBox.Text = _node.Request.RawJsonBody;
        PathListView.ItemsSource = _node.Request.PathParameters;
        QueryListView.ItemsSource = _node.Request.QueryParameters;
        HeaderListView.ItemsSource = _node.Request.Headers;
        AuthenticationKindComboBox.SelectedItem = _node.Request.Authentication.Kind;
        BearerTokenTextBox.Text = _node.Request.Authentication.BearerToken;
        BasicUsernameTextBox.Text = _node.Request.Authentication.Username;
        BasicPasswordTextBox.Text = _node.Request.Authentication.Password;
        ApiKeyNameTextBox.Text = _node.Request.Authentication.ApiKeyName;
        ApiKeyValueTextBox.Text = _node.Request.Authentication.ApiKeyValue;
        ApiKeyLocationComboBox.SelectedItem = _node.Request.Authentication.ApiKeyLocation;
        CaptureTokenCheckBox.IsChecked = _node.Request.TokenCapture.IsEnabled;
        AccessTokenPathTextBox.Text = _node.Request.TokenCapture.AccessTokenPath;
        RefreshTokenPathTextBox.Text = _node.Request.TokenCapture.RefreshTokenPath;
        ExpiresInPathTextBox.Text = _node.Request.TokenCapture.ExpiresInPath;
        ExpiresAtPathTextBox.Text = _node.Request.TokenCapture.ExpiresAtPath;
        UpdateAuthenticationVisibility();
        ValidationInfoBar.IsOpen = false;
        CompactValidationStatus.Visibility = Visibility.Collapsed;

        _bodyHistory.Reset();
        UpdateLastBodyState();
        _isLoading = false;
    }

    public void RefreshRoutePreview()
    {
        RoutePrefixTextBox.Text = _project is null || _node is null
            ? string.Empty
            : string.Join(
                "/",
                RequestUrlComposer.GetRouteTemplateParts(_project, _node)
                    .Where(part => part.IsEnabled && part.Kind != UrlSourceKind.Endpoint)
                    .Select(part => part.Value.Trim('/'))
                    .Where(part => part.Length > 0));
    }

    public void SetExecutionState(bool isRunning)
    {
        SendButton.IsEnabled = !isRunning && CurrentRequest is not null;
        ValidateButton.IsEnabled = !isRunning;
        TimeoutNumberBox.IsEnabled = !isRunning;
        CancelButton.IsEnabled = isRunning;
        CancelButton.Visibility = isRunning ? Visibility.Visible : Visibility.Collapsed;
        SendButton.Content = isRunning ? "发送中…" : "发送";
    }

    public void ShowValidation(RequestValidationResult result)
    {
        ValidationIssuesItems.ItemsSource = result.Issues
            .Select(issue => $"• {issue.Message}")
            .ToArray();
        CompactValidationStatus.Visibility = Visibility.Visible;
        CompactValidationStatusText.Text = result.IsValid
            ? "已就绪"
            : $"{result.Issues.Count} 个问题";
        CompactValidationStatusText.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
            result.IsValid ? "SystemFillColorSuccessBrush" : "SystemFillColorCriticalBrush"];
        ValidationInfoBar.Severity = InfoBarSeverity.Error;
        ValidationInfoBar.Title = "请求定义需要修正";
        ValidationInfoBar.Message = string.Empty;
        ValidationInfoBar.IsOpen = !result.IsValid;
    }

    public void ShowPrepared(PreparedRequestResult result, bool showValidation = true)
    {
        if (showValidation)
        {
            ShowValidation(result.Validation);
        }
    }

    public void FocusRouteEditor()
    {
        RouteTextBox.Focus(FocusState.Programmatic);
        RouteTextBox.SelectAll();
    }

    public void SelectSection(RequestEditorSection section)
    {
        ParamsTab.IsChecked = section == RequestEditorSection.Params;
        PathTab.IsChecked = section == RequestEditorSection.Path;
        HeadersTab.IsChecked = section == RequestEditorSection.Headers;
        BodyTab.IsChecked = section == RequestEditorSection.Body;
        AuthTab.IsChecked = section == RequestEditorSection.Auth;
        ApplySelectedSection();

        if (section == RequestEditorSection.Body)
        {
            BodyTextBox.Focus(FocusState.Programmatic);
        }
    }

    private void MethodComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || CurrentRequest is null || MethodComboBox.SelectedItem is not string method)
        {
            return;
        }

        CurrentRequest.Method = method;
        RequestChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RouteTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoading || CurrentRequest is null)
        {
            return;
        }

        CurrentRequest.Route = RouteTextBox.Text;
        OnRequestChanged();
    }

    private void BodyTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isLoading &&
            !_suppressBodyHistory &&
            CurrentRequest is not null &&
            !string.Equals(_lastBodyState.Text, BodyTextBox.Text, StringComparison.Ordinal))
        {
            _bodyHistory.Record(_lastBodyState);
        }

        UpdateLastBodyState();
        if (_isLoading || CurrentRequest is null)
        {
            return;
        }

        CurrentRequest.RawJsonBody = BodyTextBox.Text;
        OnRequestChanged();
    }

    private void BodyTextBox_SelectionChanged(object sender, RoutedEventArgs e)
    {
        UpdateLastBodyState();
        UpdateJsonCaretStatus();
    }

    private void BodyTextBox_CharacterReceived(
        UIElement sender,
        CharacterReceivedRoutedEventArgs args)
    {
        if (args.Character is '\r' or '\n')
        {
            if (_suppressNextBodyNewLineCharacter)
            {
                _suppressNextBodyNewLineCharacter = false;
            }
            else
            {
                ApplyBodyEdit(
                    JsonEditorAssist.InsertNewLine(
                        BodyTextBox.Text,
                        BodyTextBox.SelectionStart,
                        BodyTextBox.SelectionLength),
                    recordHistory: true);
            }

            args.Handled = true;
            return;
        }

        if (args.Character > char.MaxValue)
        {
            return;
        }

        // CharacterReceived runs after the TextBox has accepted the character.
        // Only add the matching delimiter; reinserting the opening character
        // produced "{{}" and "[[ ]" while typing.
        var edit = JsonEditorAssist.CompleteAlreadyInsertedCharacter(
            BodyTextBox.Text,
            BodyTextBox.SelectionStart,
            BodyTextBox.SelectionLength,
            (char)args.Character);
        if (edit is null)
        {
            return;
        }

        ApplyBodyEdit(edit, recordHistory: true);
        args.Handled = true;
    }

    private void BodyTextBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var control = IsKeyDown(VirtualKey.Control);
        var shift = IsKeyDown(VirtualKey.Shift);
        var menu = IsKeyDown(VirtualKey.Menu);
        if (shift && menu && e.Key == VirtualKey.F)
        {
            FormatJson_Click(sender, e);
            e.Handled = true;
            return;
        }
        if (!control && e.Key == VirtualKey.Tab)
        {
            ApplyBodyEdit(
                JsonEditorAssist.InsertIndentation(
                    BodyTextBox.Text,
                    BodyTextBox.SelectionStart,
                    BodyTextBox.SelectionLength),
                recordHistory: true);
            e.Handled = true;
            return;
        }
        if (!control && e.Key == (VirtualKey)0xDE)
        {
            var property = JsonEditorAssist.CompleteExistingClosingQuote(
                BodyTextBox.Text,
                BodyTextBox.SelectionStart);
            if (property is not null)
            {
                ApplyBodyEdit(property, recordHistory: true);
                e.Handled = true;
            }
            return;
        }
        if (control && e.Key == VirtualKey.Z)
        {
            if (shift)
            {
                RedoBodyEdit();
            }
            else
            {
                UndoBodyEdit();
            }

            e.Handled = true;
            return;
        }

        if (control && e.Key == VirtualKey.Y)
        {
            RedoBodyEdit();
            e.Handled = true;
            return;
        }

        if (control && e.Key == VirtualKey.Space)
        {
            ShowJsonCompletions();
            e.Handled = true;
            return;
        }

        if (!control && e.Key == VirtualKey.Enter)
        {
            _suppressNextBodyNewLineCharacter = true;
            ApplyBodyEdit(
                JsonEditorAssist.InsertNewLine(
                    BodyTextBox.Text,
                    BodyTextBox.SelectionStart,
                    BodyTextBox.SelectionLength),
                recordHistory: true);
            DispatcherQueue.TryEnqueue(() => _suppressNextBodyNewLineCharacter = false);
            e.Handled = true;
            return;
        }

        if (!control && e.Key == VirtualKey.Back)
        {
            var edit = JsonEditorAssist.BackspacePair(
                BodyTextBox.Text,
                BodyTextBox.SelectionStart,
                BodyTextBox.SelectionLength);
            if (edit is not null)
            {
                ApplyBodyEdit(edit, recordHistory: true);
                e.Handled = true;
            }
        }
    }

    private void FieldTextChanged(object sender, TextChangedEventArgs e) => OnRequestChanged();

    private void FieldChanged(object sender, RoutedEventArgs e) => OnRequestChanged();

    private void OnRequestChanged()
    {
        if (_isLoading)
        {
            return;
        }

        ValidationInfoBar.IsOpen = false;
        CompactValidationStatus.Visibility = Visibility.Collapsed;
        RequestChanged?.Invoke(this, EventArgs.Empty);
    }

    private void AddActiveField_Click(object sender, RoutedEventArgs e)
    {
        AddField(
            HeadersTab.IsChecked == true ? CurrentRequest?.Headers :
            PathTab.IsChecked == true ? CurrentRequest?.PathParameters :
            CurrentRequest?.QueryParameters);
    }

    private void AddField(ObservableCollection<RequestField>? fields)
    {
        if (fields is null)
        {
            return;
        }

        fields.Add(new RequestField());
        OnRequestChanged();
    }

    private void RemoveField_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RequestField field })
        {
            RemoveField(field);
        }
    }

    private void FieldRow_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (FocusManager.GetFocusedElement(XamlRoot) is TextBox)
        {
            return;
        }

        if (e.Key == VirtualKey.Delete && sender is FrameworkElement { DataContext: RequestField field })
        {
            RemoveField(field);
            e.Handled = true;
        }
    }

    private void QueryListView_DragItemsCompleted(
        ListViewBase sender,
        DragItemsCompletedEventArgs args)
    {
        OnRequestChanged();
        FeedbackRequested?.Invoke(
            this,
            new FeedbackEventArgs("Query parameter order updated."));
    }

    private void RemoveField(RequestField field)
    {
        if (CurrentRequest is null)
        {
            return;
        }

        if (!CurrentRequest.QueryParameters.Remove(field) &&
            !CurrentRequest.PathParameters.Remove(field))
        {
            CurrentRequest.Headers.Remove(field);
        }

        OnRequestChanged();
    }

    private void FormatJson_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentRequest is null)
        {
            return;
        }

        var result = JsonBodyFormatter.Format(CurrentRequest.RawJsonBody);
        if (!result.Succeeded)
        {
            ValidationIssuesItems.ItemsSource = new[] { $"• {result.Error}" };
            ValidationInfoBar.Severity = InfoBarSeverity.Error;
            ValidationInfoBar.Title = "无法格式化 JSON";
            ValidationInfoBar.Message = string.Empty;
            ValidationInfoBar.IsOpen = true;
            BodyTextBox.Focus(FocusState.Programmatic);
            return;
        }

        BodyTextBox.Text = result.FormattedJson;
        FeedbackRequested?.Invoke(this, new FeedbackEventArgs("JSON 已格式化。"));
    }

    private void JsonCompletionButton_Click(object sender, RoutedEventArgs e) =>
        ShowJsonCompletions();

    private void ShowJsonCompletions()
    {
        var flyout = new MenuFlyout();
        foreach (var completion in JsonEditorAssist.GetCompletions(
                     BodyTextBox.Text,
                     BodyTextBox.SelectionStart))
        {
            var item = new MenuFlyoutItem
            {
                Text = $"{completion.Label}    {completion.Description}",
                Tag = completion
            };
            item.Click += JsonCompletionItem_Click;
            flyout.Items.Add(item);
        }

        var caret = GetBodyCaretRectangle();
        flyout.ShowAt(
            BodyTextBox,
            new FlyoutShowOptions
            {
                Position = new Point(
                    Math.Max(0, caret.X),
                    Math.Max(0, caret.Bottom))
            });
    }

    private void JsonCompletionItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: JsonEditorCompletion completion })
        {
            return;
        }

        ApplyBodyEdit(
            JsonEditorAssist.ApplyCompletion(
                BodyTextBox.Text,
                BodyTextBox.SelectionStart,
                BodyTextBox.SelectionLength,
                completion),
            recordHistory: true);
        BodyTextBox.Focus(FocusState.Programmatic);
    }

    private Rect GetBodyCaretRectangle()
    {
        if (BodyTextBox.Text.Length == 0)
        {
            return new Rect(8, 8, 1, 18);
        }

        var index = Math.Clamp(BodyTextBox.SelectionStart, 0, BodyTextBox.Text.Length);
        return index == BodyTextBox.Text.Length
            ? BodyTextBox.GetRectFromCharacterIndex(index - 1, true)
            : BodyTextBox.GetRectFromCharacterIndex(index, false);
    }

    private void UndoBodyEdit()
    {
        if (_bodyHistory.TryUndo(CaptureBodyState(), out var state))
        {
            ApplyBodyEdit(state, recordHistory: false);
        }
    }

    private void RedoBodyEdit()
    {
        if (_bodyHistory.TryRedo(CaptureBodyState(), out var state))
        {
            ApplyBodyEdit(state, recordHistory: false);
        }
    }

    private void ApplyBodyEdit(TextEditorState state, bool recordHistory)
    {
        if (recordHistory)
        {
            _bodyHistory.Record(CaptureBodyState());
        }

        _suppressBodyHistory = true;
        try
        {
            BodyTextBox.Text = state.Text;
            SetBodySelection(state);
        }
        finally
        {
            _suppressBodyHistory = false;
            UpdateLastBodyState();
        }
    }

    private void SetBodySelection(TextEditorState state)
    {
        var actualText = BodyTextBox.Text;
        var selectionStart = JsonEditorAssist.MapCaretIndex(
            state.Text,
            state.SelectionStart,
            actualText);
        var selectionEnd = JsonEditorAssist.MapCaretIndex(
            state.Text,
            state.SelectionStart + state.SelectionLength,
            actualText);
        BodyTextBox.SelectionStart = selectionStart;
        BodyTextBox.SelectionLength = Math.Max(0, selectionEnd - selectionStart);
    }

    private TextEditorState CaptureBodyState() =>
        new(
            BodyTextBox.Text,
            BodyTextBox.SelectionStart,
            BodyTextBox.SelectionLength);

    private void UpdateLastBodyState() =>
        _lastBodyState = CaptureBodyState();

    private void UpdateJsonCaretStatus()
    {
        var caret = Math.Clamp(BodyTextBox.SelectionStart, 0, BodyTextBox.Text.Length);
        var before = BodyTextBox.Text[..caret].Replace("\r\n", "\n", StringComparison.Ordinal);
        var line = before.Count(character => character == '\n') + 1;
        var lastBreak = before.LastIndexOf('\n');
        var column = before.Length - lastBreak;
        JsonCaretStatusText.Text = $"Ln {line}, Col {column} · Spaces: 2";
    }

    private static bool IsKeyDown(VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;

    private void SectionTab_Click(object sender, RoutedEventArgs e)
    {
        SelectSection(
            ReferenceEquals(sender, PathTab) ? RequestEditorSection.Path :
            ReferenceEquals(sender, HeadersTab) ? RequestEditorSection.Headers :
            ReferenceEquals(sender, BodyTab) ? RequestEditorSection.Body :
            ReferenceEquals(sender, AuthTab) ? RequestEditorSection.Auth :
            RequestEditorSection.Params);
    }

    private void ApplySelectedSection()
    {
        ParamsPanel.Visibility = ParamsTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PathPanel.Visibility = PathTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        HeadersPanel.Visibility = HeadersTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        BodyPanel.Visibility = BodyTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        AuthPanel.Visibility = AuthTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        AddActiveFieldButton.Visibility =
            BodyTab.IsChecked == true || AuthTab.IsChecked == true
                ? Visibility.Collapsed
                : Visibility.Visible;
        AddActiveFieldButton.Content =
            HeadersTab.IsChecked == true ? "+ 添加 Header" :
            PathTab.IsChecked == true ? "+ 添加 Path 参数" :
            "+ 添加参数";
    }

    private void AuthenticationChanged(object sender, RoutedEventArgs e) =>
        ApplyAuthenticationEditor();

    private void AuthenticationTextChanged(object sender, TextChangedEventArgs e) =>
        ApplyAuthenticationEditor();

    private void ApplyAuthenticationEditor()
    {
        if (_isLoading || CurrentRequest is null)
        {
            return;
        }

        var authentication = CurrentRequest.Authentication;
        authentication.Kind = AuthenticationKindComboBox.SelectedItem is AuthenticationKind kind
            ? kind
            : AuthenticationKind.None;
        authentication.BearerToken = BearerTokenTextBox.Text;
        authentication.Username = BasicUsernameTextBox.Text;
        authentication.Password = BasicPasswordTextBox.Text;
        authentication.ApiKeyName = ApiKeyNameTextBox.Text;
        authentication.ApiKeyValue = ApiKeyValueTextBox.Text;
        authentication.ApiKeyLocation = ApiKeyLocationComboBox.SelectedItem is ApiKeyLocation location
            ? location
            : ApiKeyLocation.Header;
        CurrentRequest.TokenCapture.IsEnabled = CaptureTokenCheckBox.IsChecked == true;
        CurrentRequest.TokenCapture.AccessTokenPath = AccessTokenPathTextBox.Text;
        CurrentRequest.TokenCapture.RefreshTokenPath = RefreshTokenPathTextBox.Text;
        CurrentRequest.TokenCapture.ExpiresInPath = ExpiresInPathTextBox.Text;
        CurrentRequest.TokenCapture.ExpiresAtPath = ExpiresAtPathTextBox.Text;
        UpdateAuthenticationVisibility();
        OnRequestChanged();
    }

    private void UpdateAuthenticationVisibility()
    {
        var kind = AuthenticationKindComboBox.SelectedItem is AuthenticationKind value
            ? value
            : AuthenticationKind.None;
        BearerTokenTextBox.Visibility =
            kind == AuthenticationKind.BearerToken ? Visibility.Visible : Visibility.Collapsed;
        BasicUsernameTextBox.Visibility = BasicPasswordTextBox.Visibility =
            kind == AuthenticationKind.Basic ? Visibility.Visible : Visibility.Collapsed;
        ApiKeyNameTextBox.Visibility = ApiKeyValueTextBox.Visibility = ApiKeyLocationComboBox.Visibility =
            kind == AuthenticationKind.ApiKey ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ValidateButton_Click(object sender, RoutedEventArgs e)
    {
        ValidateRequested?.Invoke(this, EventArgs.Empty);
    }

    private void SendButton_Click(object sender, RoutedEventArgs e) =>
        SendRequested?.Invoke(this, EventArgs.Empty);

    private void CancelButton_Click(object sender, RoutedEventArgs e) =>
        CancelRequested?.Invoke(this, EventArgs.Empty);

}
