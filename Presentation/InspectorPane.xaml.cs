using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ProbeLoom.Core;
using System.Text;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace ProbeLoom.Presentation;

public enum InspectorPage
{
    Route,
    Variables,
    Auth,
    Inspect,
    Diagnostics
}

public sealed class InspectorEditRequestedEventArgs(RouteCompositionPart part) : EventArgs
{
    public RouteCompositionPart Part { get; } = part;
}

public sealed class RoutePartMoveRequestedEventArgs(
    RouteCompositionPart source,
    RouteCompositionPart target,
    int keyboardDelta = 0,
    bool insertAfter = false) : EventArgs
{
    public RouteCompositionPart Source { get; } = source;
    public RouteCompositionPart Target { get; } = target;
    public int KeyboardDelta { get; } = keyboardDelta;
    public bool InsertAfter { get; } = insertAfter;
}

public sealed partial class InspectorPane : UserControl
{
    private RequestInspectorSnapshot? _snapshot;
    private InspectorPage _page;
    private bool _canExpand = true;
    private RouteCompositionPart? _draggedPart;
    private IReadOnlyList<RouteCompositionPart> _routeParts = [];
    private IReadOnlyList<InspectorVariableItem> _variables = [];
    private IReadOnlyList<InspectorVariableBlock> _variableBlocks = [];

    public InspectorPane()
    {
        InitializeComponent();
        SelectPage(InspectorPage.Route);
        ShowSnapshot(null);
    }

    public event EventHandler? ExpandRequested;

    public event EventHandler? CollapseRequested;

    public event EventHandler<InspectorEditRequestedEventArgs>? EditRequested;
    public event EventHandler<RoutePartMoveRequestedEventArgs>? RoutePartMoveRequested;

    public event EventHandler<FeedbackEventArgs>? FeedbackRequested;

    public event EventHandler? DiagnosticsRequested;

    public event EventHandler? DiagnosticsCancelRequested;

    public event EventHandler? ManageVariablesRequested;

    public bool IncludeUnsafeHttpRequest =>
        IncludeUnsafeHttpCheckBox.IsChecked == true;

    public void SetLayout(bool isExpanded, bool canExpand)
    {
        _canExpand = canExpand;
        ExpandedContent.Visibility = isExpanded ? Visibility.Visible : Visibility.Collapsed;
        CollapsedRail.Visibility = isExpanded ? Visibility.Collapsed : Visibility.Visible;
        ExpandButton.IsEnabled = canExpand;
        ToolTipService.SetToolTip(
            ExpandButton,
            canExpand ? "展开 Inspector" : "增大窗口宽度后可展开 Inspector");
    }

    public void ShowSnapshot(RequestInspectorSnapshot? snapshot)
    {
        _snapshot = snapshot;
        var hasSnapshot = snapshot is not null;
        RouteEmptyState.Visibility = hasSnapshot ? Visibility.Collapsed : Visibility.Visible;
        RoutePartsItems.Visibility = hasSnapshot ? Visibility.Visible : Visibility.Collapsed;
        FinalUrlTextBox.Text = snapshot?.FinalUrl ?? string.Empty;
        UpdateRouteParts(snapshot?.RouteParts ?? []);
        UpdateVariables(snapshot?.Variables ?? []);
        UpdateVariableBlocks(snapshot?.VariableBlocks ?? []);

        ValidationStatusText.Text = snapshot is null
            ? "无请求"
            : snapshot.IsValid ? "Ready" : $"{snapshot.ValidationMessages.Count} errors";
        ValidationStatusText.Foreground = snapshot is null
            ? (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            : snapshot.IsValid
                ? (Brush)Application.Current.Resources["ProbeLoomSuccessBrush"]
                : (Brush)Application.Current.Resources["ProbeLoomDangerBrush"];

        AuthMethodText.Text = snapshot?.Authentication.Method ?? "No Auth";
        AuthSourceText.Text = snapshot?.Authentication.Source ?? "选择请求后显示认证来源";
        TokenExistsText.Text = snapshot?.Authentication.TokenExists == true ? "已设置" : "未设置";
        TokenExpiryText.Text = snapshot?.Authentication.TokenExpired == true
            ? "已过期"
            : snapshot?.Authentication.TokenExists == true ? "有效或未知" : "—";
        RefreshConfiguredText.Text = snapshot?.Authentication.RefreshConfigured == true ? "已配置" : "未配置";
        TokenStatusText.Text = snapshot?.Authentication.TokenStatus ?? string.Empty;

        InspectMethodText.Text = snapshot?.RequestMethod ?? "—";
        InspectUrlText.Text = snapshot?.FinalUrl ?? "选择请求后显示最终地址";
        InspectCountsText.Text = snapshot is null
            ? string.Empty
            : $"{snapshot.HeaderCount} Headers · {snapshot.BodyCharacterCount:N0} Body chars";
        InspectAuthText.Text = snapshot?.FinalRequest is { } finalRequest
            ? $"{finalRequest.Authentication} · Content-Type: {Display(finalRequest.ContentType)}"
            : string.Empty;
        InspectTimingText.Text = snapshot?.FinalRequest is { } timingRequest
            ? $"Timeout {timingRequest.Timeout.TotalSeconds:0.##} s · Content-Length {timingRequest.ContentLength:N0} bytes"
            : string.Empty;
        InspectHeadersItems.ItemsSource = snapshot?.FinalRequest?.Headers ?? [];
        InspectBodyText.Text = snapshot?.FinalRequest?.Body ?? string.Empty;
        InspectBodySourceText.Text = snapshot?.FinalRequest is { } bodyRequest
            ? bodyRequest.BodyContainsMaskedValues
                ? "来源：Request Body · Variable / Secret（敏感值已掩码）"
                : "来源：Request Body"
            : string.Empty;
        InspectCurlText.Text = !string.IsNullOrWhiteSpace(snapshot?.PowerShellCurl)
            ? snapshot.PowerShellCurl
            : snapshot?.CurlExportError ?? string.Empty;
        RunDiagnosticsButton.IsEnabled = snapshot?.FinalRequest is not null;
        ValidationMessagesItems.ItemsSource = snapshot is null
            ? []
            : snapshot.ValidationMessages.Count == 0
                ? ["请求定义校验通过。"]
                : snapshot.ValidationMessages.Select(message => $"• {message}").ToArray();
    }

    private void UpdateRouteParts(IReadOnlyList<RouteCompositionPart> routeParts)
    {
        if (_routeParts.SequenceEqual(routeParts))
        {
            return;
        }

        _routeParts = routeParts.ToArray();
        RoutePartsItems.ItemsSource = _routeParts.Select(part => new RoutePartView(part)).ToArray();
    }

    private void UpdateVariables(IReadOnlyList<InspectorVariableItem> variables)
    {
        if (_variables.SequenceEqual(variables))
        {
            return;
        }

        _variables = variables.ToArray();
        VariablesList.ItemsSource = _variables.Select(item => new VariableItemView(item)).ToArray();
    }

    private void UpdateVariableBlocks(IReadOnlyList<InspectorVariableBlock> variableBlocks)
    {
        if (_variableBlocks.SequenceEqual(variableBlocks))
        {
            return;
        }

        _variableBlocks = variableBlocks.ToArray();
        VariableBlocksItems.ItemsSource = _variableBlocks
            .Select(item => new VariableBlockView(item))
            .ToArray();
    }

    public void SelectPage(InspectorPage page)
    {
        _page = page;
        RouteTab.IsChecked = page == InspectorPage.Route;
        VariablesTab.IsChecked = page == InspectorPage.Variables;
        AuthTab.IsChecked = page == InspectorPage.Auth;
        InspectTab.IsChecked = page == InspectorPage.Inspect;
        DiagnosticsTab.IsChecked = page == InspectorPage.Diagnostics;
        RoutePage.Visibility = page == InspectorPage.Route ? Visibility.Visible : Visibility.Collapsed;
        VariablesPage.Visibility = page == InspectorPage.Variables ? Visibility.Visible : Visibility.Collapsed;
        AuthPage.Visibility = page == InspectorPage.Auth ? Visibility.Visible : Visibility.Collapsed;
        InspectPage.Visibility = page == InspectorPage.Inspect ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsPage.Visibility = page == InspectorPage.Diagnostics ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Expand_Click(object sender, RoutedEventArgs e)
    {
        if (_canExpand)
        {
            ExpandRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Collapse_Click(object sender, RoutedEventArgs e) =>
        CollapseRequested?.Invoke(this, EventArgs.Empty);

    private void ManageVariables_Click(object sender, RoutedEventArgs e) =>
        ManageVariablesRequested?.Invoke(this, EventArgs.Empty);

    private void CollapsedPage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string pageName } &&
            Enum.TryParse<InspectorPage>(pageName, out var page))
        {
            SelectPage(page);
            if (_canExpand)
            {
                ExpandRequested?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private void PageTab_Click(object sender, RoutedEventArgs e)
    {
        SelectPage(
            ReferenceEquals(sender, VariablesTab) ? InspectorPage.Variables :
            ReferenceEquals(sender, AuthTab) ? InspectorPage.Auth :
            ReferenceEquals(sender, InspectTab) ? InspectorPage.Inspect :
            ReferenceEquals(sender, DiagnosticsTab) ? InspectorPage.Diagnostics :
            InspectorPage.Route);
    }

    public void SetDiagnosticsRunning(bool isRunning)
    {
        RunDiagnosticsButton.IsEnabled = !isRunning && _snapshot?.FinalRequest is not null;
        CancelDiagnosticsButton.IsEnabled = isRunning;
        if (isRunning)
        {
            var includeHttp = _snapshot?.RequestMethod is "GET" or "HEAD" or "OPTIONS" ||
                              IncludeUnsafeHttpRequest;
            DiagnosticsStatusText.Text = includeHttp
                ? "正在执行 DNS、TCP、TLS 和 HTTP 分阶段诊断…"
                : "正在执行 DNS、TCP 和 TLS 诊断；为避免副作用，本次不发送 HTTP 请求…";
        }
    }

    public void ShowDiagnostics(NetworkDiagnosticResult? result)
    {
        if (result is null)
        {
            DiagnosticsStatusText.Text = "尚未为当前请求运行详细诊断。";
            DiagnosticsOutputText.Text = string.Empty;
            return;
        }

        DiagnosticsStatusText.Text = result.IsCancelled
            ? "诊断已取消"
            : $"诊断完成 · {result.Duration.TotalMilliseconds:N0} ms";
        DiagnosticsOutputText.Text = BuildDiagnosticSummary(result);
    }

    public void ShowDiagnosticValidation(RequestValidationResult validation)
    {
        DiagnosticsStatusText.Text = "请求未通过校验，未执行网络诊断。";
        DiagnosticsOutputText.Text = string.Join(
            "\r\n",
            validation.Issues.Select(issue => $"• {issue.Message}"));
    }

    public void ShowDiagnosticError(string title, string detail)
    {
        DiagnosticsStatusText.Text = title;
        DiagnosticsOutputText.Text = detail;
    }

    private void RoutePart_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RoutePartView view })
        {
            EditRequested?.Invoke(this, new InspectorEditRequestedEventArgs(view.Part));
        }
    }

    private void RoutePart_DragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: RoutePartView view } || !view.IsMovable)
        {
            args.Cancel = true;
            return;
        }

        args.Data.SetText(view.Part.Key);
        _draggedPart = view.Part;
        args.Data.RequestedOperation = DataPackageOperation.Move;
        args.DragUI.SetContentFromDataPackage();
    }

    private void VariableBlock_DragStarting(UIElement sender, DragStartingEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: VariableBlockView view })
        {
            args.Cancel = true;
            return;
        }

        VariableDropBehavior.SetDragData(args.Data, view.Name);
        args.AllowedOperations = DataPackageOperation.Copy;
        VariableDropBehavior.HideDragVisual(args.DragUI);
    }

    private void RoutePartsItems_DragOver(object sender, DragEventArgs e)
    {
        var targetElement = FindViewElement(e.OriginalSource as DependencyObject);
        var target = targetElement?.DataContext as RoutePartView;
        if (!TryGetDraggedPart(e, out var source) ||
            targetElement is null ||
            target is null ||
            !CanDrop(source, target.Part))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            RouteDropHintText.Text = "Locked structural block — cannot drop here";
            RouteDropHintText.Visibility = Visibility.Visible;
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Move;
        var after = e.GetPosition(targetElement).X > targetElement.ActualWidth / 2;
        e.DragUIOverride.Caption = $"Insert {(after ? "after" : "before")} {target.DisplayValue}";
        RouteDropHintText.Text = $"Drop to insert {(after ? "after" : "before")} {target.SourceCaption}";
        RouteDropHintText.Visibility = Visibility.Visible;
    }

    private async void RoutePartsItems_Drop(object sender, DragEventArgs e)
    {
        try
        {
            var targetElement = FindViewElement(e.OriginalSource as DependencyObject);
            var target = targetElement?.DataContext as RoutePartView;
            var key = await e.DataView.GetTextAsync();
            var source = _snapshot?.RouteParts.FirstOrDefault(part => part.Key == key);
            if (source is not null &&
                targetElement is not null &&
                target is not null &&
                CanDrop(source, target.Part))
            {
                RoutePartMoveRequested?.Invoke(
                    this,
                    new RoutePartMoveRequestedEventArgs(
                        source,
                        target.Part,
                        insertAfter: e.GetPosition(targetElement).X > targetElement.ActualWidth / 2));
            }
        }
        finally
        {
            _draggedPart = null;
            RouteDropHintText.Visibility = Visibility.Collapsed;
        }
    }

    private void RoutePartsItems_DragLeave(object sender, DragEventArgs e) =>
        RouteDropHintText.Visibility = Visibility.Collapsed;

    private void RoutePart_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RoutePartView view } ||
            !view.IsMovable ||
            !e.KeyStatus.IsMenuKeyDown ||
            e.Key is not (VirtualKey.Up or VirtualKey.Down or VirtualKey.Left or VirtualKey.Right))
        {
            return;
        }

        var delta = e.Key is VirtualKey.Up or VirtualKey.Left ? -1 : 1;
        RoutePartMoveRequested?.Invoke(
            this,
            new RoutePartMoveRequestedEventArgs(view.Part, view.Part, delta));
        e.Handled = true;
    }

    private bool TryGetDraggedPart(DragEventArgs e, out RouteCompositionPart part)
    {
        part = _draggedPart!;
        return part is not null && e.DataView.Contains(StandardDataFormats.Text);
    }

    private static FrameworkElement? FindViewElement(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is FrameworkElement { DataContext: RoutePartView })
            {
                return (FrameworkElement)source;
            }
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    private static bool CanDrop(RouteCompositionPart source, RouteCompositionPart target) =>
        RouteReorderService.CanMove(source.Kind) &&
        source.Kind == target.Kind &&
        source.Key != target.Key;

    private void CopyFinalUrl_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_snapshot?.FinalUrl))
        {
            FeedbackRequested?.Invoke(this, new FeedbackEventArgs("当前没有可复制的最终 URL。"));
            return;
        }

        var package = new DataPackage();
        package.SetText(_snapshot.FinalUrl);
        Clipboard.SetContent(package);
        Clipboard.Flush();
        FeedbackRequested?.Invoke(this, new FeedbackEventArgs("最终 URL 已复制。"));
    }

    private void CopyCurl_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_snapshot?.PowerShellCurl))
        {
            FeedbackRequested?.Invoke(this, new FeedbackEventArgs(
                _snapshot?.CurlExportError ?? "当前没有可复制的 curl 命令。"));
            return;
        }

        CopyText(_snapshot.PowerShellCurl);
        FeedbackRequested?.Invoke(this, new FeedbackEventArgs("已复制掩码后的 PowerShell curl 命令。"));
    }

    private void RunDiagnostics_Click(object sender, RoutedEventArgs e) =>
        DiagnosticsRequested?.Invoke(this, EventArgs.Empty);

    private void CancelDiagnostics_Click(object sender, RoutedEventArgs e) =>
        DiagnosticsCancelRequested?.Invoke(this, EventArgs.Empty);

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(DiagnosticsOutputText.Text))
        {
            FeedbackRequested?.Invoke(this, new FeedbackEventArgs("当前没有可复制的诊断摘要。"));
            return;
        }

        CopyText(DiagnosticsOutputText.Text);
        FeedbackRequested?.Invoke(this, new FeedbackEventArgs("诊断摘要已复制。"));
    }

    private static string BuildDiagnosticSummary(NetworkDiagnosticResult result)
    {
        var text = new StringBuilder();
        text.AppendLine($"Started: {result.StartedAt:O}");
        text.AppendLine($"Total: {result.Duration.TotalMilliseconds:N0} ms");
        text.AppendLine();
        text.AppendLine($"DNS  {result.Dns.State}  {result.Dns.Duration.TotalMilliseconds:N0} ms");
        foreach (var address in result.Dns.Addresses)
        {
            var family = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                ? "IPv4"
                : "IPv6";
            text.AppendLine($"  {address}  ({family})");
        }
        if (!string.IsNullOrWhiteSpace(result.Dns.Error))
        {
            text.AppendLine($"  {result.Dns.FailureKind}: {result.Dns.Error}");
        }

        text.AppendLine();
        text.AppendLine("TCP");
        foreach (var attempt in result.TcpAttempts)
        {
            text.AppendLine(
                $"  {attempt.Address}:{attempt.Port}  {attempt.State}  {attempt.Duration.TotalMilliseconds:N0} ms" +
                (string.IsNullOrWhiteSpace(attempt.Error)
                    ? string.Empty
                    : $"  {attempt.FailureKind}: {attempt.Error}"));
        }

        if (result.Tls is { } tls)
        {
            text.AppendLine();
            text.AppendLine(
                $"TLS  {tls.State}  TCP {tls.TcpConnectionDuration.TotalMilliseconds:N0} ms · " +
                $"handshake {tls.HandshakeDuration.TotalMilliseconds:N0} ms  {tls.Protocol}");
            if (tls.Certificate is { } certificate)
            {
                text.AppendLine($"  Subject: {certificate.Subject}");
                text.AppendLine($"  Issuer: {certificate.Issuer}");
                text.AppendLine($"  Valid: {certificate.NotBefore:g} — {certificate.NotAfter:g}");
                text.AppendLine($"  Host name: {(certificate.HostNameMatches ? "match" : "mismatch")}");
                text.AppendLine($"  SAN: {Display(certificate.SubjectAlternativeNames)}");
                foreach (var error in certificate.ChainErrors)
                {
                    text.AppendLine($"  Chain: {error}");
                }
            }
            if (!string.IsNullOrWhiteSpace(tls.Error))
            {
                text.AppendLine($"  {tls.FailureKind}: {tls.Error}");
            }
        }

        text.AppendLine();
        if (result.Http.Execution is { } http)
        {
            text.AppendLine($"HTTP  {http.State}  {http.StatusCode} {http.ReasonPhrase}");
            text.AppendLine($"  Headers: {FormatTime(http.Timing.HeadersReceived)}");
            text.AppendLine($"  First byte: {FormatTime(http.Timing.FirstByte)}");
            text.AppendLine($"  Complete: {http.Timing.Total.TotalMilliseconds:N0} ms");
            text.AppendLine($"  Size: {http.ResponseSizeBytes:N0} bytes");
            text.AppendLine($"  Final URL: {http.FinalUrl}");
            text.AppendLine($"  Redirects: {http.RedirectChain.Count}");
            foreach (var hop in http.RedirectChain)
            {
                text.AppendLine($"    {hop.Sequence}. {hop.StatusCode} {hop.Url}");
                text.AppendLine(
                    $"       Location: {hop.Location}");
                text.AppendLine(
                    $"       → {hop.TargetUrl}  {hop.Duration.TotalMilliseconds:N0} ms" +
                    (hop.SensitiveHeadersRemoved ? "  sensitive headers removed" : string.Empty));
            }
        }
        else
        {
            text.AppendLine($"HTTP  {result.Http.State}: {result.Http.Error}");
        }

        if (result.Suggestions.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("建议");
            foreach (var suggestion in result.Suggestions)
            {
                text.AppendLine($"  • {suggestion}");
            }
        }
        return text.ToString().TrimEnd();
    }

    private static string FormatTime(TimeSpan? value) =>
        value is null ? "未测量" : $"{value.Value.TotalMilliseconds:N0} ms";

    private static string Display(string value) =>
        string.IsNullOrWhiteSpace(value) ? "未设置" : value;

    private static void CopyText(string value)
    {
        var package = new DataPackage();
        package.SetText(value);
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }

    private sealed class RoutePartView
    {
        public RoutePartView(RouteCompositionPart part)
        {
            Part = part;
            StatusBrush = part.State switch
            {
                RouteCompositionPartState.Active =>
                    (Brush)Application.Current.Resources["ProbeLoomSuccessBrush"],
                RouteCompositionPartState.Disabled =>
                    (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
                _ => (Brush)Application.Current.Resources["ProbeLoomDangerBrush"]
            };
        }

        public RouteCompositionPart Part { get; }
        public string AutomationName =>
            $"编辑 {Part.SourceType} {Part.SourceName}：{DisplayValue}";
        public string EditToolTip => $"定位到 {Part.SourceType} · {Part.SourceName} 的编辑入口";
        public string Glyph => Part.State switch
        {
            RouteCompositionPartState.Active => "\uE73E",
            RouteCompositionPartState.Disabled => "\uE711",
            RouteCompositionPartState.Missing => "\uE783",
            _ => "\uEA39"
        };
        public Brush StatusBrush { get; }
        public double Opacity => Part.IsEnabled ? 1 : 0.52;
        public string SourceCaption => $"{Part.SourceType} · {Part.SourceName}";
        public string DisplayValue => string.IsNullOrWhiteSpace(Part.ResolvedValue)
            ? "未配置"
            : Part.ResolvedValue;
        public bool ShowTemplate =>
            !string.IsNullOrWhiteSpace(Part.TemplateValue) &&
            (!string.Equals(Part.TemplateValue, Part.ResolvedValue, StringComparison.Ordinal) ||
             Part.TemplateValue.Contains("{{", StringComparison.Ordinal));
        public string TemplateCaption => $"模板：{Part.TemplateValue}";
        public bool ShowEncoding =>
            !string.IsNullOrWhiteSpace(Part.EncodedValue) &&
            !string.Equals(Part.ResolvedValue, Part.EncodedValue, StringComparison.Ordinal);
        public string EncodingCaption => $"编码：{Part.EncodedValue}";
        public bool HasStatus => !string.IsNullOrWhiteSpace(Part.StatusMessage);
        public string StatusMessage => Part.StatusMessage;
        public bool IsMovable => RouteReorderService.CanMove(Part.Kind);
        public string InteractionGlyph => IsMovable ? "\uE7C2" : "\uE72E";
        public string InteractionHint => IsMovable
            ? "Drag to reorder; Alt+Left/Right also moves"
            : "Order is locked by workspace structure";
    }

    private sealed class VariableItemView(InspectorVariableItem item)
    {
        public string Name { get; } = item.Name;
        public string DisplayValue { get; } = item.DisplayValue;
        public string Source { get; } = item.Source;
        public bool IsSecret { get; } = item.IsSecret;
        public bool HasDetail { get; } =
            item.HasError || item.IsOverridden || (item.IsSecret && !item.IsConfigured);
        public string Detail { get; } = item.HasError
            ? item.ErrorMessage
            : item.IsSecret && !item.IsConfigured
                ? "Secret 尚未配置"
                : item.IsOverridden
                    ? $"覆盖：{item.OverrideSummary}"
                    : string.Empty;
    }

    private sealed class VariableBlockView(InspectorVariableBlock item)
    {
        public string Name { get; } = item.Name;
        public string Reference { get; } = VariableReference.Format(item.Name);
        public bool IsSecret { get; } = item.IsSecret;
        public string AutomationName { get; } =
            $"变量积木 {item.Name}，拖动以插入引用";
        public string ToolTip { get; } =
            $"{item.Source}\r\n" +
            (item.IsSecret
                ? item.IsConfigured ? "Secret 已配置；拖动只插入引用" : "Secret 未配置"
                : "拖动到文本编辑器以插入引用") +
            (item.IsReferenced ? "\r\n当前请求已引用" : string.Empty) +
            (string.IsNullOrWhiteSpace(item.Detail) ? string.Empty : $"\r\n{item.Detail}");
    }
}
