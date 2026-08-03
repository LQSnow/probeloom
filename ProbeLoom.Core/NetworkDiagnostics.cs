using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace ProbeLoom.Core;

public enum DiagnosticStageState
{
    NotRun,
    Running,
    Succeeded,
    Failed,
    Cancelled
}

public enum DiagnosticFailureKind
{
    None,
    DnsNotFound,
    ConnectionRefused,
    Timeout,
    NetworkUnreachable,
    TlsCertificate,
    TlsHandshake,
    Http,
    Cancelled,
    Other
}

public sealed record DnsDiagnosticResult(
    DiagnosticStageState State,
    TimeSpan Duration,
    IReadOnlyList<IPAddress> Addresses,
    DiagnosticFailureKind FailureKind,
    string Error);

public sealed record TcpAttemptResult(
    IPAddress Address,
    int Port,
    DiagnosticStageState State,
    TimeSpan Duration,
    DiagnosticFailureKind FailureKind,
    string Error);

public sealed record TlsCertificateDetails(
    string Subject,
    string Issuer,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter,
    string SubjectAlternativeNames,
    bool HostNameMatches,
    IReadOnlyList<string> ChainErrors);

public sealed record TlsDiagnosticResult(
    DiagnosticStageState State,
    TimeSpan HandshakeDuration,
    SslProtocols Protocol,
    TlsCertificateDetails? Certificate,
    DiagnosticFailureKind FailureKind,
    string Error,
    TimeSpan TcpConnectionDuration = default)
{
    public TimeSpan Duration => HandshakeDuration;
    public TimeSpan TotalDuration => TcpConnectionDuration + HandshakeDuration;
}

public sealed record HttpDiagnosticResult(
    DiagnosticStageState State,
    HttpExecutionResult? Execution,
    DiagnosticFailureKind FailureKind,
    string Error);

public sealed record NetworkDiagnosticResult(
    Guid Id,
    Guid RequestNodeId,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    DnsDiagnosticResult Dns,
    IReadOnlyList<TcpAttemptResult> TcpAttempts,
    TlsDiagnosticResult? Tls,
    HttpDiagnosticResult Http,
    IReadOnlyList<string> Suggestions,
    bool IsCancelled);

public interface IDnsDiagnosticProbe
{
    Task<DnsDiagnosticResult> ResolveAsync(string host, CancellationToken cancellationToken);
}

public interface ITcpDiagnosticProbe
{
    Task<TcpAttemptResult> ConnectAsync(
        IPAddress address,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

public interface ITlsDiagnosticProbe
{
    Task<TlsDiagnosticResult> HandshakeAsync(
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

public sealed class SystemDnsDiagnosticProbe : IDnsDiagnosticProbe
{
    public async Task<DnsDiagnosticResult> ResolveAsync(
        string host,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken)
                .ConfigureAwait(false);
            stopwatch.Stop();
            return new DnsDiagnosticResult(
                DiagnosticStageState.Succeeded,
                stopwatch.Elapsed,
                addresses,
                DiagnosticFailureKind.None,
                string.Empty);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return new DnsDiagnosticResult(
                DiagnosticStageState.Cancelled,
                stopwatch.Elapsed,
                [],
                DiagnosticFailureKind.Cancelled,
                "DNS 解析已取消。");
        }
        catch (SocketException exception)
        {
            stopwatch.Stop();
            return new DnsDiagnosticResult(
                DiagnosticStageState.Failed,
                stopwatch.Elapsed,
                [],
                Classify(exception.SocketErrorCode),
                exception.Message);
        }
    }

    public static DiagnosticFailureKind Classify(SocketError error) =>
        error is SocketError.HostNotFound or SocketError.NoData
            ? DiagnosticFailureKind.DnsNotFound
            : DiagnosticFailureKind.Other;
}

public sealed class SystemTcpDiagnosticProbe : ITcpDiagnosticProbe
{
    public async Task<TcpAttemptResult> ConnectAsync(
        IPAddress address,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), linked.Token)
                .ConfigureAwait(false);
            stopwatch.Stop();
            return new TcpAttemptResult(
                address, port, DiagnosticStageState.Succeeded, stopwatch.Elapsed,
                DiagnosticFailureKind.None, string.Empty);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new TcpAttemptResult(
                address, port, DiagnosticStageState.Cancelled, stopwatch.Elapsed,
                DiagnosticFailureKind.Cancelled, "TCP 连接已取消。");
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return new TcpAttemptResult(
                address, port, DiagnosticStageState.Failed, stopwatch.Elapsed,
                DiagnosticFailureKind.Timeout, "TCP 连接超时。");
        }
        catch (SocketException exception)
        {
            stopwatch.Stop();
            var kind = Classify(exception.SocketErrorCode);
            return new TcpAttemptResult(
                address, port, DiagnosticStageState.Failed, stopwatch.Elapsed,
                kind, exception.Message);
        }
    }

    public static DiagnosticFailureKind Classify(SocketError error) =>
        error switch
            {
                SocketError.ConnectionRefused => DiagnosticFailureKind.ConnectionRefused,
                SocketError.TimedOut => DiagnosticFailureKind.Timeout,
                SocketError.NetworkUnreachable or SocketError.HostUnreachable =>
                    DiagnosticFailureKind.NetworkUnreachable,
                _ => DiagnosticFailureKind.Other
            };
}

public sealed class SystemTlsDiagnosticProbe : ITlsDiagnosticProbe
{
    public async Task<TlsDiagnosticResult> HandshakeAsync(
        string host,
        int port,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var tcpStopwatch = Stopwatch.StartNew();
        var handshakeStopwatch = new Stopwatch();
        TlsCertificateDetails? details = null;
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, linked.Token).ConfigureAwait(false);
            tcpStopwatch.Stop();
            using var stream = new SslStream(
                client.GetStream(),
                leaveInnerStreamOpen: false,
                (_, certificate, chain, errors) =>
                {
                    details = CreateCertificateDetails(certificate, chain, errors);
                    return errors == SslPolicyErrors.None;
                });
            handshakeStopwatch.Start();
            await stream.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = host,
                    CertificateRevocationCheckMode = X509RevocationMode.Online
                },
                linked.Token).ConfigureAwait(false);
            handshakeStopwatch.Stop();
            return new TlsDiagnosticResult(
                DiagnosticStageState.Succeeded,
                handshakeStopwatch.Elapsed,
                stream.SslProtocol,
                details,
                DiagnosticFailureKind.None,
                string.Empty,
                tcpStopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            tcpStopwatch.Stop();
            handshakeStopwatch.Stop();
            return new TlsDiagnosticResult(
                DiagnosticStageState.Cancelled, handshakeStopwatch.Elapsed, SslProtocols.None,
                details, DiagnosticFailureKind.Cancelled, "TLS 握手已取消。", tcpStopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            tcpStopwatch.Stop();
            handshakeStopwatch.Stop();
            return new TlsDiagnosticResult(
                DiagnosticStageState.Failed, handshakeStopwatch.Elapsed, SslProtocols.None,
                details, DiagnosticFailureKind.Timeout, "TLS 连接或握手超时。", tcpStopwatch.Elapsed);
        }
        catch (AuthenticationException exception)
        {
            tcpStopwatch.Stop();
            handshakeStopwatch.Stop();
            return new TlsDiagnosticResult(
                DiagnosticStageState.Failed, handshakeStopwatch.Elapsed, SslProtocols.None,
                details,
                details?.ChainErrors.Count > 0 || details?.HostNameMatches == false
                    ? DiagnosticFailureKind.TlsCertificate
                    : DiagnosticFailureKind.TlsHandshake,
                exception.Message,
                tcpStopwatch.Elapsed);
        }
        catch (Exception exception) when (exception is SocketException or IOException)
        {
            tcpStopwatch.Stop();
            handshakeStopwatch.Stop();
            return new TlsDiagnosticResult(
                DiagnosticStageState.Failed, handshakeStopwatch.Elapsed, SslProtocols.None,
                details, DiagnosticFailureKind.TlsHandshake, exception.Message, tcpStopwatch.Elapsed);
        }
        catch (Exception exception)
        {
            tcpStopwatch.Stop();
            handshakeStopwatch.Stop();
            return new TlsDiagnosticResult(
                DiagnosticStageState.Failed, handshakeStopwatch.Elapsed, SslProtocols.None,
                details, DiagnosticFailureKind.Other, exception.Message, tcpStopwatch.Elapsed);
        }
    }

    public static TlsCertificateDetails? CreateCertificateDetails(
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors errors)
    {
        if (certificate is null)
        {
            return null;
        }

        var value = certificate as X509Certificate2 ?? new X509Certificate2(certificate);
        var san = value.Extensions
            .OfType<X509Extension>()
            .FirstOrDefault(extension => extension.Oid?.Value == "2.5.29.17")
            ?.Format(multiLine: false) ?? string.Empty;
        var chainErrors = chain?.ChainStatus
            .Where(status => status.Status != X509ChainStatusFlags.NoError)
            .Select(status => string.IsNullOrWhiteSpace(status.StatusInformation)
                ? status.Status.ToString()
                : $"{status.Status}: {status.StatusInformation.Trim()}")
            .ToList() ?? [];
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
        {
            chainErrors.Add("未提供远程证书。");
        }
        if (errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors) &&
            chainErrors.Count == 0)
        {
            chainErrors.Add("证书链验证失败。");
        }

        return new TlsCertificateDetails(
            value.Subject,
            value.Issuer,
            value.NotBefore,
            value.NotAfter,
            san,
            !errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch),
            chainErrors);
    }
}

public sealed class NetworkDiagnosticService
{
    private readonly IDnsDiagnosticProbe _dns;
    private readonly ITcpDiagnosticProbe _tcp;
    private readonly ITlsDiagnosticProbe _tls;
    private readonly HttpRequestExecutor _http;

    public NetworkDiagnosticService(
        IDnsDiagnosticProbe? dns = null,
        ITcpDiagnosticProbe? tcp = null,
        ITlsDiagnosticProbe? tls = null,
        HttpRequestExecutor? http = null)
    {
        _dns = dns ?? new SystemDnsDiagnosticProbe();
        _tcp = tcp ?? new SystemTcpDiagnosticProbe();
        _tls = tls ?? new SystemTlsDiagnosticProbe();
        _http = http ?? new HttpRequestExecutor();
    }

    public async Task<NetworkDiagnosticResult> DiagnoseAsync(
        HttpRequestPlan plan,
        bool tokenExpired = false,
        CancellationToken cancellationToken = default,
        bool executeHttp = false)
    {
        try
        {
            return await DiagnoseCoreAsync(
                plan, tokenExpired, cancellationToken, executeHttp).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var cancelled = exception is OperationCanceledException ||
                            cancellationToken.IsCancellationRequested;
            var state = cancelled ? DiagnosticStageState.Cancelled : DiagnosticStageState.Failed;
            var kind = cancelled ? DiagnosticFailureKind.Cancelled : DiagnosticFailureKind.Other;
            var dns = new DnsDiagnosticResult(state, TimeSpan.Zero, [], kind, exception.Message);
            var http = new HttpDiagnosticResult(state, null, kind, exception.Message);
            var result = new NetworkDiagnosticResult(
                Guid.NewGuid(), plan.RequestNodeId, DateTimeOffset.Now, TimeSpan.Zero,
                dns, [], null, http, [], cancelled);
            return result with
            {
                Suggestions = cancelled
                    ? []
                    : ["诊断遇到未分类错误；请求未继续执行，可查看错误详情后重试。"]
            };
        }
    }

    private async Task<NetworkDiagnosticResult> DiagnoseCoreAsync(
        HttpRequestPlan plan,
        bool tokenExpired,
        CancellationToken cancellationToken,
        bool executeHttp)
    {
        var startedAt = DateTimeOffset.Now;
        var total = Stopwatch.StartNew();
        var dns = await _dns.ResolveAsync(plan.Uri.Host, cancellationToken).ConfigureAwait(false);
        var tcpAttempts = new List<TcpAttemptResult>();
        if (dns.State == DiagnosticStageState.Succeeded)
        {
            foreach (var address in dns.Addresses)
            {
                var attempt = await _tcp.ConnectAsync(
                    address,
                    plan.Uri.Port,
                    TimeSpan.FromSeconds(Math.Min(5, plan.Timeout.TotalSeconds)),
                    cancellationToken).ConfigureAwait(false);
                tcpAttempts.Add(attempt);
                if (attempt.State == DiagnosticStageState.Cancelled)
                {
                    break;
                }
            }
        }

        TlsDiagnosticResult? tls = null;
        if (plan.Uri.Scheme == Uri.UriSchemeHttps &&
            tcpAttempts.Any(attempt => attempt.State == DiagnosticStageState.Succeeded) &&
            !cancellationToken.IsCancellationRequested)
        {
            tls = await _tls.HandshakeAsync(
                plan.Uri.Host,
                plan.Uri.Port,
                TimeSpan.FromSeconds(Math.Min(10, plan.Timeout.TotalSeconds)),
                cancellationToken).ConfigureAwait(false);
        }

        HttpExecutionResult? execution = null;
        if (executeHttp &&
            tcpAttempts.Any(attempt => attempt.State == DiagnosticStageState.Succeeded) &&
            (tls is null || tls.State == DiagnosticStageState.Succeeded) &&
            !cancellationToken.IsCancellationRequested)
        {
            execution = await _http.ExecuteAsync(plan, cancellationToken).ConfigureAwait(false);
        }

        var http = execution is null
            ? new HttpDiagnosticResult(
                cancellationToken.IsCancellationRequested
                    ? DiagnosticStageState.Cancelled
                    : DiagnosticStageState.NotRun,
                null,
                cancellationToken.IsCancellationRequested
                    ? DiagnosticFailureKind.Cancelled
                    : DiagnosticFailureKind.None,
                cancellationToken.IsCancellationRequested
                    ? "HTTP 诊断已取消。"
                    : executeHttp
                        ? "前置网络阶段未通过，因此未执行 HTTP 请求。"
                        : "本次诊断未执行 HTTP 请求，避免在未明确授权时重复产生副作用。")
            : new HttpDiagnosticResult(
                execution.State == HttpExecutionState.Succeeded
                    ? DiagnosticStageState.Succeeded
                    : execution.State == HttpExecutionState.Cancelled
                        ? DiagnosticStageState.Cancelled
                        : DiagnosticStageState.Failed,
                execution,
                execution.State == HttpExecutionState.Succeeded
                    ? DiagnosticFailureKind.None
                    : execution.ErrorKind == HttpErrorKind.Timeout
                        ? DiagnosticFailureKind.Timeout
                        : DiagnosticFailureKind.Http,
                execution.ErrorDetail);
        total.Stop();
        var draft = new NetworkDiagnosticResult(
            Guid.NewGuid(),
            plan.RequestNodeId,
            startedAt,
            total.Elapsed,
            dns,
            tcpAttempts,
            tls,
            http,
            [],
            cancellationToken.IsCancellationRequested);
        return draft with { Suggestions = DiagnosticSuggestionEngine.Create(draft, plan, tokenExpired) };
    }
}

public static class DiagnosticSuggestionEngine
{
    public static IReadOnlyList<string> Create(
        NetworkDiagnosticResult result,
        HttpRequestPlan plan,
        bool tokenExpired = false)
    {
        var suggestions = new List<string>();
        if (result.Dns.FailureKind == DiagnosticFailureKind.DnsNotFound)
        {
            suggestions.Add("DNS 无法解析该主机名；请检查主机名或本机 DNS 配置。");
        }
        if (result.TcpAttempts.Any(item => item.FailureKind == DiagnosticFailureKind.ConnectionRefused))
        {
            suggestions.Add("目标端口拒绝连接；服务可能未启动或未监听该端口。");
        }
        if (result.TcpAttempts.Any(item => item.FailureKind == DiagnosticFailureKind.NetworkUnreachable))
        {
            suggestions.Add("目标网络不可达；请检查路由、VPN 或防火墙状态。");
        }
        if (result.Tls?.Certificate is { HostNameMatches: false })
        {
            suggestions.Add("TLS 证书与请求主机名不匹配。");
        }
        if (result.Tls?.Certificate is { ChainErrors.Count: > 0 })
        {
            suggestions.Add("TLS 证书链验证失败；请检查信任链和中间证书。");
        }
        if (result.Tls?.Certificate is { } certificate &&
            certificate.NotAfter < DateTimeOffset.Now)
        {
            suggestions.Add("TLS 证书已过期。");
        }
        if (result.Tls?.FailureKind == DiagnosticFailureKind.TlsHandshake &&
            plan.Uri.Scheme == Uri.UriSchemeHttps)
        {
            suggestions.Add("TLS 握手失败；若该端口只提供明文 HTTP，请改用 http://。");
        }
        if (result.Http.Execution?.ErrorKind == HttpErrorKind.Redirect)
        {
            suggestions.Add("请求发生重定向循环或超过重定向上限。");
        }
        if (result.Http.Execution?.ErrorKind == HttpErrorKind.Timeout ||
            result.TcpAttempts.Any(item => item.FailureKind == DiagnosticFailureKind.Timeout))
        {
            suggestions.Add("请求或连接已超时；请检查服务负载、网络路径和超时设置。");
        }
        if (result.Http.Execution?.StatusCode is >= 400 and <= 599)
        {
            suggestions.Add($"网络连接成功，服务器明确返回 HTTP {result.Http.Execution.StatusCode}；应从请求内容或服务端日志继续排查。");
        }
        if (tokenExpired)
        {
            suggestions.Add("当前 Environment Token 已过期；请刷新或重新登录后再发送。");
        }
        if (!string.IsNullOrEmpty(plan.Body))
        {
            var contentType = plan.GetEffectiveHeaders().FirstOrDefault(header =>
                string.Equals(header.Name, "Content-Type", StringComparison.OrdinalIgnoreCase))?.Value;
            var looksLikeJson = plan.Body.TrimStart().StartsWith('{') ||
                                plan.Body.TrimStart().StartsWith('[');
            if (string.IsNullOrWhiteSpace(contentType))
            {
                suggestions.Add("请求包含 Body，但没有 Content-Type。");
            }
            else if (looksLikeJson &&
                     !contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
            {
                suggestions.Add("Body 看起来是 JSON，但 Content-Type 未声明 JSON。");
            }
        }
        return suggestions;
    }
}

public sealed class DiagnosticResultStore
{
    private readonly Dictionary<Guid, NetworkDiagnosticResult> _results = [];

    public Guid? CurrentRequestNodeId { get; private set; }

    public void Select(Guid? requestNodeId) => CurrentRequestNodeId = requestNodeId;

    public bool TryStore(NetworkDiagnosticResult result)
    {
        _results[result.RequestNodeId] = result;
        return CurrentRequestNodeId == result.RequestNodeId;
    }

    public void Clear()
    {
        _results.Clear();
        CurrentRequestNodeId = null;
    }

    public NetworkDiagnosticResult? Current() =>
        CurrentRequestNodeId is Guid id && _results.TryGetValue(id, out var result)
            ? result
            : null;
}
