using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProbeLoom.Core;

namespace ProbeLoom.Presentation;

public sealed partial class DiagnosticsPane : UserControl
{
    private RequestHistory? _history;
    private HttpExecutionResult? _currentResult;
    private Guid? _currentRequestNodeId;

    public DiagnosticsPane()
    {
        InitializeComponent();
    }

    public event EventHandler? ClearHistoryRequested;

    public void SetHistory(RequestHistory history)
    {
        _history = history;
        RefreshHistory();
    }

    public void ShowForRequest(Guid? requestNodeId)
    {
        _currentRequestNodeId = requestNodeId;
        var result = requestNodeId is Guid id ? _history?.LatestFor(id) : null;
        if (result is null)
        {
            ResetResponse(requestNodeId is null ? "选择一个请求后发送。" : "这个请求在本次会话中尚未发送。");
            return;
        }

        ShowExecution(result);
    }

    public void ShowRunning(string requestName, string url)
    {
        _currentResult = null;
        StatusText.Text = "发送中";
        StatusText.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
        StatusCodeText.Text = string.Empty;
        DurationText.Text = string.Empty;
        SizeText.Text = string.Empty;
        EmptyState.Visibility = Visibility.Collapsed;
        OutputTextBox.Visibility = Visibility.Visible;
        HistoryList.Visibility = Visibility.Collapsed;
        OutputTextBox.Text = $"正在发送 {requestName}\r\n{url}\r\n\r\n可使用“取消”停止请求。";
    }

    public void ShowExecution(HttpExecutionResult result)
    {
        _currentResult = result;
        _currentRequestNodeId = result.RequestNodeId;
        StatusText.Text = result.State switch
        {
            HttpExecutionState.Succeeded when result.IsSuccessStatusCode => "成功",
            HttpExecutionState.Succeeded => "HTTP 错误",
            HttpExecutionState.TimedOut => "超时",
            HttpExecutionState.Cancelled => "已取消",
            _ => "失败"
        };
        StatusText.Foreground = result.State == HttpExecutionState.Succeeded && result.IsSuccessStatusCode
            ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ProbeLoomSuccessBrush"]
            : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ProbeLoomDangerBrush"];
        StatusCodeText.Text = result.StatusCode is int statusCode
            ? $"{statusCode} {result.ReasonPhrase}".Trim()
            : string.Empty;
        DurationText.Text = $"{result.Duration.TotalMilliseconds:N0} ms";
        SizeText.Text = result.ResponseSizeBytes > 0 ? FormatBytes(result.ResponseSizeBytes) : string.Empty;
        RefreshHistory();
        SelectBodyTab();
        RenderCurrentTab();
    }

    public void ShowValidation(RequestValidationResult result, string requestName)
    {
        StatusText.Text = result.IsValid ? "校验通过" : "需要修正";
        StatusText.Foreground = result.IsValid
            ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ProbeLoomSuccessBrush"]
            : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ProbeLoomDangerBrush"];
        StatusCodeText.Text = string.Empty;
        DurationText.Text = string.Empty;
        SizeText.Text = string.Empty;
        EmptyState.Visibility = Visibility.Collapsed;
        OutputTextBox.Visibility = Visibility.Visible;
        HistoryList.Visibility = Visibility.Collapsed;
        var lines = new List<string> { requestName, result.FinalUrl, string.Empty };
        lines.AddRange(result.Issues.Select(issue => $"• {issue.Message}"));
        lines.AddRange(result.Notes.Select(note => $"• {note}"));
        OutputTextBox.Text = string.Join("\r\n", lines);
    }

    public void ShowMessage(string status, string message, bool isError = false)
    {
        StatusText.Text = status;
        StatusText.Foreground = isError
            ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ProbeLoomDangerBrush"]
            : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
        StatusCodeText.Text = string.Empty;
        DurationText.Text = string.Empty;
        SizeText.Text = string.Empty;
        EmptyState.Visibility = Visibility.Collapsed;
        OutputTextBox.Visibility = Visibility.Visible;
        HistoryList.Visibility = Visibility.Collapsed;
        OutputTextBox.Text = message;
    }

    public void Reset()
    {
        _currentRequestNodeId = null;
        _currentResult = null;
        ResetResponse("选择一个请求后发送。");
    }

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        BodyTab.IsChecked = ReferenceEquals(sender, BodyTab);
        HeadersTab.IsChecked = ReferenceEquals(sender, HeadersTab);
        RawTab.IsChecked = ReferenceEquals(sender, RawTab);
        RedirectsTab.IsChecked = ReferenceEquals(sender, RedirectsTab);
        HistoryTab.IsChecked = ReferenceEquals(sender, HistoryTab);
        RenderCurrentTab();
    }

    private void RenderCurrentTab()
    {
        if (HistoryTab.IsChecked == true)
        {
            EmptyState.Visibility = _history?.Entries.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
            EmptyTitleText.Text = "本次会话没有历史";
            EmptyDescriptionText.Text = "发送请求后，最近结果会显示在这里。";
            OutputTextBox.Visibility = Visibility.Collapsed;
            HistoryList.Visibility = _history?.Entries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        HistoryList.Visibility = Visibility.Collapsed;
        if (_currentResult is null)
        {
            EmptyState.Visibility = Visibility.Visible;
            OutputTextBox.Visibility = Visibility.Collapsed;
            return;
        }

        EmptyState.Visibility = Visibility.Collapsed;
        OutputTextBox.Visibility = Visibility.Visible;
        OutputTextBox.Text = BodyTab.IsChecked == true
            ? BuildBodyText(_currentResult)
            : HeadersTab.IsChecked == true
                ? BuildHeadersText(_currentResult)
                : RedirectsTab.IsChecked == true
                    ? BuildRedirectText(_currentResult)
                    : _currentResult.RawBody;
    }

    private void HistoryList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is HistoryEntryView entry)
        {
            ShowExecution(entry.Result);
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        ClearHistoryRequested?.Invoke(this, EventArgs.Empty);
        RefreshHistory();
        ShowForRequest(_currentRequestNodeId);
    }

    private void RefreshHistory()
    {
        HistoryList.ItemsSource = _history?.Entries.Select(result => new HistoryEntryView(result)).ToArray() ?? [];
    }

    private void ResetResponse(string description)
    {
        StatusText.Text = "尚未发送";
        StatusText.ClearValue(TextBlock.ForegroundProperty);
        StatusCodeText.Text = string.Empty;
        DurationText.Text = string.Empty;
        SizeText.Text = string.Empty;
        EmptyTitleText.Text = "尚无响应";
        EmptyDescriptionText.Text = description;
        EmptyState.Visibility = Visibility.Visible;
        OutputTextBox.Visibility = Visibility.Collapsed;
        HistoryList.Visibility = Visibility.Collapsed;
        OutputTextBox.Text = string.Empty;
        SelectBodyTab();
    }

    private void SelectBodyTab()
    {
        BodyTab.IsChecked = true;
        HeadersTab.IsChecked = false;
        RawTab.IsChecked = false;
        RedirectsTab.IsChecked = false;
        HistoryTab.IsChecked = false;
    }

    private static string BuildBodyText(HttpExecutionResult result)
    {
        if (result.State != HttpExecutionState.Succeeded)
        {
            return $"{result.ErrorTitle}\r\n\r\n{result.ErrorDetail}\r\n\r\n{result.Method} {result.Url}";
        }

        var suffix = result.IsBodyTruncated
            ? $"\r\n\r\n— 响应超过捕获上限，仅显示前 {HttpRequestExecutor.DefaultMaximumBodyBytes:N0} bytes —"
            : string.Empty;
        return result.DisplayBody + suffix;
    }

    private static string BuildHeadersText(HttpExecutionResult result) =>
        result.ResponseHeaders.Count == 0
            ? "响应没有 Headers。"
            : string.Join("\r\n", result.ResponseHeaders.Select(header => $"{header.Name}: {header.Value}"));

    private static string BuildRedirectText(HttpExecutionResult result)
    {
        if (result.RedirectChain.Count == 0)
        {
            return $"No redirects.\r\nFinal URL: {result.FinalUrl}";
        }

        var lines = result.RedirectChain.SelectMany(hop => new[]
        {
            $"{hop.Sequence}. HTTP {hop.StatusCode}  {hop.Duration.TotalMilliseconds:N0} ms",
            $"   {hop.Url}",
            $"   Location: {hop.Location}",
            $"   → {hop.TargetUrl}" +
            (hop.SensitiveHeadersRemoved ? "  [跨主机，敏感 Headers 已移除]" : string.Empty)
        });
        return string.Join("\r\n", lines.Append($"Final URL: {result.FinalUrl}"));
    }

    private static string FormatBytes(long bytes) =>
        bytes < 1024 ? $"{bytes} B" :
        bytes < 1024 * 1024 ? $"{bytes / 1024d:N1} KB" :
        $"{bytes / 1024d / 1024d:N1} MB";

    private sealed class HistoryEntryView
    {
        public HistoryEntryView(HttpExecutionResult result)
        {
            Result = result;
        }

        public HttpExecutionResult Result { get; }
        public string Status => Result.StatusCode?.ToString() ?? Result.State switch
        {
            HttpExecutionState.TimedOut => "TIMEOUT",
            HttpExecutionState.Cancelled => "CANCEL",
            _ => "ERROR"
        };
        public string Title => $"{Result.Method}  {Result.RequestName}";
        public string Url => Result.Url;
        public string Time => Result.StartedAt.ToString("HH:mm:ss");
        public string Duration => $"{Result.Duration.TotalMilliseconds:N0} ms";
    }
}
