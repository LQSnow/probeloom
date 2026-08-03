using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace ProbeLoom.Core;

public sealed class HttpRequestExecutor : IDisposable
{
    public const int DefaultMaximumBodyBytes = 5 * 1024 * 1024;
    public const int DefaultMaximumRedirects = 10;
    private readonly HttpClient _client;
    private readonly int _maximumBodyBytes;
    private readonly int _maximumRedirects;
    private bool _disposed;

    public HttpRequestExecutor(
        HttpMessageHandler? handler = null,
        int maximumBodyBytes = DefaultMaximumBodyBytes,
        int maximumRedirects = DefaultMaximumRedirects)
    {
        if (maximumBodyBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBodyBytes));
        }
        if (maximumRedirects < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRedirects));
        }

        handler ??= new SocketsHttpHandler { AllowAutoRedirect = false };
        _client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        _maximumBodyBytes = maximumBodyBytes;
        _maximumRedirects = maximumRedirects;
    }

    public async Task<HttpExecutionResult> ExecuteAsync(
        HttpRequestPlan plan,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var startedAt = DateTimeOffset.Now;
        var stopwatch = Stopwatch.StartNew();
        using var timeoutSource = new CancellationTokenSource(plan.Timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);
        var redirects = new List<HttpRedirectHop>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            NormalizeRedirectKey(plan.Uri)
        };

        try
        {
            var currentUri = plan.Uri;
            var currentMethod = plan.Method;
            var currentBody = plan.Body;
            var currentHeaders = plan.Headers.ToList();
            while (true)
            {
                var hopStarted = stopwatch.Elapsed;
                using var request = BuildRequest(currentMethod, currentUri, currentHeaders, currentBody);
                using var response = await _client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    linkedSource.Token).ConfigureAwait(false);
                var headersElapsed = stopwatch.Elapsed;
                if (!IsRedirect(response.StatusCode) || response.Headers.Location is null)
                {
                    var body = await ReadBodyAsync(
                        response.Content,
                        stopwatch,
                        linkedSource.Token).ConfigureAwait(false);
                    var contentType = response.Content.Headers.ContentType;
                    var formatted = ResponseBodyFormatter.Format(
                        body.Bytes,
                        contentType?.MediaType,
                        contentType?.CharSet);
                    var headers = response.Headers
                        .Concat(response.Content.Headers)
                        .SelectMany(header =>
                            header.Value.Select(value => new HttpHeaderValue(header.Key, value)))
                        .ToArray();
                    stopwatch.Stop();
                    return new HttpExecutionResult(
                        Guid.NewGuid(),
                        plan.RequestNodeId,
                        plan.RequestName,
                        plan.Method,
                        plan.DisplayUrl,
                        startedAt,
                        stopwatch.Elapsed,
                        HttpExecutionState.Succeeded,
                        (int)response.StatusCode,
                        response.ReasonPhrase ?? string.Empty,
                        headers,
                        body.TotalSize,
                        contentType?.ToString() ?? string.Empty,
                        formatted.Kind,
                        formatted.DisplayBody,
                        formatted.RawBody,
                        body.IsTruncated,
                        HttpErrorKind.None,
                        string.Empty,
                        string.Empty)
                    {
                        FinalUrl = SensitiveDataMasker.MaskUri(currentUri).AbsoluteUri,
                        RedirectChain = redirects,
                        Timing = new HttpPhaseTiming(
                            startedAt,
                            headersElapsed,
                            body.FirstByteElapsed,
                            stopwatch.Elapsed)
                    };
                }

                var nextUri = ResolveRedirectUri(currentUri, response.Headers.Location);
                var crossAuthority = !SameAuthority(currentUri, nextUri);
                redirects.Add(new HttpRedirectHop(
                    redirects.Count + 1,
                    (int)response.StatusCode,
                    SensitiveDataMasker.MaskUri(currentUri).AbsoluteUri,
                    MaskLocation(response.Headers.Location, nextUri),
                    SensitiveDataMasker.MaskUri(nextUri).AbsoluteUri,
                    stopwatch.Elapsed - hopStarted,
                    crossAuthority));
                if (redirects.Count > _maximumRedirects)
                {
                    return RedirectFailure(
                        plan, startedAt, stopwatch, redirects,
                        "重定向次数过多",
                        $"请求超过了 {_maximumRedirects} 次重定向上限。");
                }

                if (!visited.Add(NormalizeRedirectKey(nextUri)))
                {
                    return RedirectFailure(
                        plan, startedAt, stopwatch, redirects,
                        "检测到重定向循环",
                        $"重定向再次指向已访问的地址：{SensitiveDataMasker.MaskUri(nextUri).AbsoluteUri}");
                }

                if (crossAuthority)
                {
                    currentHeaders.RemoveAll(header =>
                        SensitiveDataMasker.IsSensitiveHeader(header.Name));
                }

                if (ShouldSwitchToGet(response.StatusCode, currentMethod))
                {
                    currentMethod = "GET";
                    currentBody = string.Empty;
                    currentHeaders.RemoveAll(header =>
                        header.Name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase));
                }
                currentUri = nextUri;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(plan, startedAt, stopwatch, redirects, HttpExecutionState.Cancelled,
                HttpErrorKind.Cancelled, "请求已取消", "请求已由用户取消。");
        }
        catch (OperationCanceledException)
        {
            return Failure(plan, startedAt, stopwatch, redirects, HttpExecutionState.TimedOut,
                HttpErrorKind.Timeout, "请求超时",
                $"服务器未能在 {plan.Timeout.TotalSeconds:0.##} 秒内完成响应。");
        }
        catch (HttpRequestException exception)
        {
            var (kind, title) = Classify(exception.HttpRequestError);
            return Failure(plan, startedAt, stopwatch, redirects, HttpExecutionState.Failed,
                kind, title, exception.Message);
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException or UriFormatException)
        {
            return Failure(plan, startedAt, stopwatch, redirects, HttpExecutionState.Failed,
                HttpErrorKind.InvalidRequest, "请求无法构建", exception.Message);
        }
        catch (Exception exception)
        {
            return Failure(plan, startedAt, stopwatch, redirects, HttpExecutionState.Failed,
                HttpErrorKind.Other, "请求执行失败", exception.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _client.Dispose();
    }

    private static HttpRequestMessage BuildRequest(
        string method,
        Uri uri,
        IReadOnlyList<HttpHeaderValue> headers,
        string body)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), uri);
        var hasContentHeaders = headers.Any(header =>
            header.Name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(body) || hasContentHeaders)
        {
            request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        }

        foreach (var header in headers)
        {
            if (request.Headers.TryAddWithoutValidation(header.Name, header.Value))
            {
                continue;
            }
            request.Content ??= new ByteArrayContent([]);
            if (!request.Content.Headers.TryAddWithoutValidation(header.Name, header.Value))
            {
                throw new FormatException($"无法应用 Header“{header.Name}”。");
            }
        }

        if (!string.IsNullOrEmpty(body) && request.Content?.Headers.ContentType is null)
        {
            request.Content!.Headers.ContentType = new MediaTypeHeaderValue("application/json")
            {
                CharSet = "utf-8"
            };
        }
        return request;
    }

    private async Task<BodyReadResult> ReadBodyAsync(
        HttpContent content,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream(Math.Min(_maximumBodyBytes, 64 * 1024));
        var buffer = new byte[16 * 1024];
        var totalRead = 0L;
        var truncated = false;
        TimeSpan? firstByte = null;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            firstByte ??= stopwatch.Elapsed;
            totalRead += read;
            var remaining = _maximumBodyBytes - (int)output.Length;
            if (remaining > 0)
            {
                output.Write(buffer, 0, Math.Min(read, remaining));
            }
            if (totalRead > _maximumBodyBytes)
            {
                truncated = true;
                break;
            }
        }

        var declaredLength = content.Headers.ContentLength;
        return new BodyReadResult(
            output.ToArray(),
            declaredLength is > 0 ? declaredLength.Value : totalRead,
            truncated || declaredLength > _maximumBodyBytes,
            firstByte);
    }

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect or
            HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or
            HttpStatusCode.PermanentRedirect;

    private static bool ShouldSwitchToGet(HttpStatusCode statusCode, string method) =>
        statusCode == HttpStatusCode.RedirectMethod ||
        statusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect &&
        string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase);

    private static Uri ResolveRedirectUri(Uri current, Uri location) =>
        location.IsAbsoluteUri ? location : new Uri(current, location);

    private static string MaskLocation(Uri location, Uri resolved)
    {
        var masked = SensitiveDataMasker.MaskUri(resolved);
        return location.IsAbsoluteUri
            ? masked.AbsoluteUri
            : masked.PathAndQuery + masked.Fragment;
    }

    private static bool SameAuthority(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase) &&
        left.Port == right.Port;

    private static string NormalizeRedirectKey(Uri uri) =>
        uri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.SafeUnescaped);

    private static HttpExecutionResult RedirectFailure(
        HttpRequestPlan plan,
        DateTimeOffset startedAt,
        Stopwatch stopwatch,
        IReadOnlyList<HttpRedirectHop> redirects,
        string title,
        string detail) =>
        Failure(plan, startedAt, stopwatch, redirects, HttpExecutionState.Failed,
            HttpErrorKind.Redirect, title, detail);

    private static HttpExecutionResult Failure(
        HttpRequestPlan plan,
        DateTimeOffset startedAt,
        Stopwatch stopwatch,
        IReadOnlyList<HttpRedirectHop> redirects,
        HttpExecutionState state,
        HttpErrorKind kind,
        string title,
        string detail)
    {
        stopwatch.Stop();
        return new HttpExecutionResult(
            Guid.NewGuid(), plan.RequestNodeId, plan.RequestName, plan.Method, plan.DisplayUrl,
            startedAt, stopwatch.Elapsed, state, null, string.Empty, [], 0, string.Empty,
            HttpResponseContentKind.Empty, string.Empty, string.Empty, false, kind, title, detail)
        {
            RedirectChain = redirects.ToArray(),
            Timing = new HttpPhaseTiming(startedAt, null, null, stopwatch.Elapsed)
        };
    }

    private static (HttpErrorKind Kind, string Title) Classify(HttpRequestError error) => error switch
    {
        HttpRequestError.SecureConnectionError => (HttpErrorKind.Tls, "TLS 安全连接失败"),
        HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError =>
            (HttpErrorKind.Connection, "无法连接到服务器"),
        HttpRequestError.InvalidResponse or HttpRequestError.ResponseEnded or
            HttpRequestError.VersionNegotiationError =>
            (HttpErrorKind.Protocol, "服务器响应无效"),
        _ => (HttpErrorKind.Other, "HTTP 请求失败")
    };

    private sealed record BodyReadResult(
        byte[] Bytes,
        long TotalSize,
        bool IsTruncated,
        TimeSpan? FirstByteElapsed);
}
