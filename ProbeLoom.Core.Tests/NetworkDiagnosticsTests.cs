namespace ProbeLoom.Core.Tests;

internal static partial class CoreTests
{
    static async Task ProbesDnsAndTcp()
    {
        var dns = await new SystemDnsDiagnosticProbe().ResolveAsync("localhost", CancellationToken.None);
        Equal(DiagnosticStageState.Succeeded, dns.State);
        True(dns.Addresses.Count > 0, "localhost did not resolve.");
        Equal(
            DiagnosticFailureKind.DnsNotFound,
            SystemDnsDiagnosticProbe.Classify(SocketError.HostNotFound));

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accept = listener.AcceptTcpClientAsync();
        var tcp = new SystemTcpDiagnosticProbe();
        var success = await tcp.ConnectAsync(
            IPAddress.Loopback, port, TimeSpan.FromSeconds(2), CancellationToken.None);
        using var accepted = await accept;
        listener.Stop();
        Equal(DiagnosticStageState.Succeeded, success.State);

        var refusedListener = new TcpListener(IPAddress.Loopback, 0);
        refusedListener.Start();
        var closedPort = ((IPEndPoint)refusedListener.LocalEndpoint).Port;
        refusedListener.Stop();
        var refused = await tcp.ConnectAsync(
            IPAddress.Loopback, closedPort, TimeSpan.FromSeconds(2), CancellationToken.None);
        Equal(DiagnosticStageState.Failed, refused.State);
        True(
            refused.FailureKind is DiagnosticFailureKind.ConnectionRefused or DiagnosticFailureKind.Timeout,
            "Closed local port did not produce a connection failure.");
        Equal(
            DiagnosticFailureKind.ConnectionRefused,
            SystemTcpDiagnosticProbe.Classify(SocketError.ConnectionRefused));
        Equal(
            DiagnosticFailureKind.Timeout,
            SystemTcpDiagnosticProbe.Classify(SocketError.TimedOut));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var timedOrCancelled = await tcp.ConnectAsync(
            IPAddress.Parse("192.0.2.1"), 81, TimeSpan.FromSeconds(5), cancelled.Token);
        Equal(DiagnosticFailureKind.Cancelled, timedOrCancelled.FailureKind);
    }

    static void ExtractsTlsCertificateDiagnostics()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=wrong.example.test",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("wrong.example.test");
        request.CertificateExtensions.Add(san.Build());
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(30));
        var details = SystemTlsDiagnosticProbe.CreateCertificateDetails(
            certificate,
            null,
            SslPolicyErrors.RemoteCertificateNameMismatch |
            SslPolicyErrors.RemoteCertificateChainErrors)!;
        True(!details.HostNameMatches, "TLS hostname mismatch was not captured.");
        True(details.Subject.Contains("wrong.example.test"), "Certificate subject was not captured.");
        True(details.SubjectAlternativeNames.Contains("wrong.example.test"), "SAN was not captured.");
        True(details.ChainErrors.Count > 0, "Certificate chain failure was not captured.");
    }

    static void MapsDiagnosticSuggestions()
    {
        var (_, _, _, plan) = CreateHttpPlan();
        var execution = CreateHistoryResult(plan.RequestNodeId, "failure") with
        {
            StatusCode = 503,
            ErrorKind = HttpErrorKind.None
        };
        var result = new NetworkDiagnosticResult(
            Guid.NewGuid(),
            plan.RequestNodeId,
            DateTimeOffset.Now,
            TimeSpan.FromMilliseconds(20),
            new DnsDiagnosticResult(
                DiagnosticStageState.Succeeded,
                TimeSpan.FromMilliseconds(1),
                [IPAddress.Loopback],
                DiagnosticFailureKind.None,
                string.Empty),
            [
                new TcpAttemptResult(
                    IPAddress.Loopback,
                    5080,
                    DiagnosticStageState.Failed,
                    TimeSpan.FromMilliseconds(1),
                    DiagnosticFailureKind.ConnectionRefused,
                    "refused")
            ],
            null,
            new HttpDiagnosticResult(
                DiagnosticStageState.Succeeded,
                execution,
                DiagnosticFailureKind.None,
                string.Empty),
            [],
            false);
        var suggestions = DiagnosticSuggestionEngine.Create(result, plan, tokenExpired: true);
        True(suggestions.Any(item => item.Contains("未启动") || item.Contains("未监听")),
            "Connection-refused advice was not produced.");
        True(suggestions.Any(item => item.Contains("HTTP 503")),
            "HTTP status advice was not produced.");
        True(suggestions.Any(item => item.Contains("Token") && item.Contains("过期")),
            "Expired-token advice was not produced.");
    }

    static async Task CancelsAndIsolatesDiagnostics()
    {
        var (_, _, _, firstPlan) = CreateHttpPlan();
        var secondId = Guid.NewGuid();
        var store = new DiagnosticResultStore();
        store.Select(secondId);
        var firstResult = new NetworkDiagnosticResult(
            Guid.NewGuid(),
            firstPlan.RequestNodeId,
            DateTimeOffset.Now,
            TimeSpan.Zero,
            new DnsDiagnosticResult(
                DiagnosticStageState.Cancelled,
                TimeSpan.Zero,
                [],
                DiagnosticFailureKind.Cancelled,
                "cancelled"),
            [],
            null,
            new HttpDiagnosticResult(
                DiagnosticStageState.Cancelled,
                null,
                DiagnosticFailureKind.Cancelled,
                "cancelled"),
            [],
            true);
        True(!store.TryStore(firstResult), "Old request result should not target the selected request.");
        True(store.Current() is null, "Old diagnostic result leaked into the current request.");

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var service = new NetworkDiagnosticService(
            new BlockingDnsProbe(),
            new StubTcpProbe(),
            new StubTlsProbe(),
            new HttpRequestExecutor(new StubHandler((_, _) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)))));
        var cancelled = await service.DiagnoseAsync(firstPlan, cancellationToken: cancellation.Token);
        True(cancelled.IsCancelled, "Diagnostic cancellation was not preserved.");
        Equal(DiagnosticStageState.Cancelled, cancelled.Dns.State);
        Equal(DiagnosticStageState.Cancelled, cancelled.Http.State);
    }

    static async Task AvoidsUnsafeDiagnosticReplay()
    {
        var (_, _, _, plan) = CreateHttpPlan();
        var calls = 0;
        var service = new NetworkDiagnosticService(
            new BlockingDnsProbe(),
            new StubTcpProbe(),
            new StubTlsProbe(),
            new HttpRequestExecutor(new StubHandler((_, _) =>
            {
                calls++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            })));

        var result = await service.DiagnoseAsync(plan);
        Equal(0, calls);
        Equal(DiagnosticStageState.NotRun, result.Http.State);
        True(result.Http.Error.Contains("未执行 HTTP", StringComparison.Ordinal), "Skipped HTTP stage was not explained.");

        await service.DiagnoseAsync(plan, executeHttp: true);
        Equal(1, calls);

        var failed = await new NetworkDiagnosticService(
            new ThrowingDnsProbe(),
            new StubTcpProbe(),
            new StubTlsProbe()).DiagnoseAsync(plan);
        Equal(DiagnosticFailureKind.Other, failed.Dns.FailureKind);
    }

}
