namespace ProbeLoom.Core.Tests;

internal static partial class CoreTests
{
    static Func<Task> Sync(Action action) => () =>
    {
        action();
        return Task.CompletedTask;
    };

    static HttpExecutionResult CreateHistoryResult(Guid nodeId, string name) =>
        new(
            Guid.NewGuid(),
            nodeId,
            name,
            "GET",
            "https://example.test",
            DateTimeOffset.Now,
            TimeSpan.FromMilliseconds(10),
            HttpExecutionState.Succeeded,
            200,
            "OK",
            [],
            0,
            string.Empty,
            HttpResponseContentKind.Empty,
            string.Empty,
            string.Empty,
            false,
            HttpErrorKind.None,
            string.Empty,
            string.Empty);

    static async Task WithTemporaryDirectory(Func<string, Task> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "ProbeLoom.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await action(directory);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    static async Task ThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
        }
    }

    static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    sealed class CountingSecureValueStore(string value) : ISecureValueStore
    {
        private int _getCalls;

        public int GetCalls => Volatile.Read(ref _getCalls);

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _getCalls);
            return Task.FromResult<string?>(value);
        }

        public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    sealed class BlockingFirstSecureValueStore(string value) : ISecureValueStore
    {
        private int _getCalls;

        public TaskCompletionSource<bool> FirstReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int GetCalls => Volatile.Read(ref _getCalls);

        public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _getCalls);
            if (call == 1)
            {
                FirstReadStarted.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return value;
        }

        public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }

    sealed class BlockingDnsProbe : IDnsDiagnosticProbe
    {
        public Task<DnsDiagnosticResult> ResolveAsync(string host, CancellationToken cancellationToken) =>
            Task.FromResult(new DnsDiagnosticResult(
                cancellationToken.IsCancellationRequested
                    ? DiagnosticStageState.Cancelled
                    : DiagnosticStageState.Succeeded,
                TimeSpan.Zero,
                cancellationToken.IsCancellationRequested ? [] : [IPAddress.Loopback],
                cancellationToken.IsCancellationRequested
                    ? DiagnosticFailureKind.Cancelled
                    : DiagnosticFailureKind.None,
                cancellationToken.IsCancellationRequested ? "cancelled" : string.Empty));
    }

    sealed class ThrowingDnsProbe : IDnsDiagnosticProbe
    {
        public Task<DnsDiagnosticResult> ResolveAsync(string host, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("controlled diagnostic failure");
    }

    sealed class StubTcpProbe : ITcpDiagnosticProbe
    {
        public Task<TcpAttemptResult> ConnectAsync(
            IPAddress address,
            int port,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            Task.FromResult(new TcpAttemptResult(
                address,
                port,
                cancellationToken.IsCancellationRequested
                    ? DiagnosticStageState.Cancelled
                    : DiagnosticStageState.Succeeded,
                TimeSpan.Zero,
                cancellationToken.IsCancellationRequested
                    ? DiagnosticFailureKind.Cancelled
                    : DiagnosticFailureKind.None,
                string.Empty));
    }

    sealed class StubTlsProbe : ITlsDiagnosticProbe
    {
        public Task<TlsDiagnosticResult> HandshakeAsync(
            string host,
            int port,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            Task.FromResult(new TlsDiagnosticResult(
                DiagnosticStageState.Succeeded,
                TimeSpan.Zero,
                System.Security.Authentication.SslProtocols.Tls13,
                null,
                DiagnosticFailureKind.None,
                string.Empty));
    }

}
