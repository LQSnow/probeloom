namespace ProbeLoom.Core;

public enum RouteCatalogRefreshMode
{
    Immediate,
    Debounced
}

public sealed record RouteCatalogRefreshResult(
    long Revision,
    ProjectDocument? Project,
    RouteCatalog? Catalog);

public sealed class RouteCatalogService : IDisposable
{
    private readonly object _sync = new();
    private readonly ISecureValueStore _secureValueStore;
    private readonly TimeSpan _debounceDelay;
    private CancellationTokenSource? _refreshCancellation;
    private long _revision;
    private bool _disposed;

    public RouteCatalogService(
        ISecureValueStore secureValueStore,
        TimeSpan? debounceDelay = null)
    {
        ArgumentNullException.ThrowIfNull(secureValueStore);
        var delay = debounceDelay ?? TimeSpan.FromMilliseconds(200);
        if (delay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(debounceDelay),
                "Route Catalog debounce delay cannot be negative.");
        }

        _secureValueStore = secureValueStore;
        _debounceDelay = delay;
    }

    public async Task<RouteCatalogRefreshResult?> RefreshAsync(
        ProjectDocument? project,
        ProjectEnvironment? environment,
        TokenSession? tokenSession,
        RouteCatalogRefreshMode mode = RouteCatalogRefreshMode.Debounced,
        CancellationToken cancellationToken = default)
    {
        CancellationTokenSource refreshCancellation;
        CancellationTokenSource? previousCancellation;
        long revision;
        lock (_sync)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(RouteCatalogService));
            }
            previousCancellation = _refreshCancellation;
            refreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _refreshCancellation = refreshCancellation;
            revision = ++_revision;
        }

        CancelNoThrow(previousCancellation);
        var refreshToken = refreshCancellation.Token;

        try
        {
            if (mode == RouteCatalogRefreshMode.Debounced && _debounceDelay > TimeSpan.Zero)
            {
                await Task.Delay(_debounceDelay, refreshToken).ConfigureAwait(false);
            }

            if (project is null)
            {
                return IsCurrentRevision(revision)
                    ? new RouteCatalogRefreshResult(revision, null, null)
                    : null;
            }

            var catalog = await RouteCatalogBuilder.BuildAsync(
                project,
                environment,
                _secureValueStore,
                tokenSession,
                refreshToken).ConfigureAwait(false);
            refreshToken.ThrowIfCancellationRequested();

            return IsCurrentRevision(revision)
                ? new RouteCatalogRefreshResult(revision, project, catalog)
                : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested &&
                                !IsCurrentRevision(revision))
        {
            return null;
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_refreshCancellation, refreshCancellation))
                {
                    _refreshCancellation = null;
                }
            }
            refreshCancellation.Dispose();
        }
    }

    public bool IsCurrent(RouteCatalogRefreshResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return IsCurrentRevision(result.Revision);
    }

    public void Cancel()
    {
        CancellationTokenSource? cancellation;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            cancellation = _refreshCancellation;
            _refreshCancellation = null;
            _revision++;
        }

        CancelNoThrow(cancellation);
    }

    public void Dispose()
    {
        CancellationTokenSource? cancellation;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            cancellation = _refreshCancellation;
            _refreshCancellation = null;
            _revision++;
        }

        CancelNoThrow(cancellation);
    }

    private static void CancelNoThrow(CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private bool IsCurrentRevision(long revision)
    {
        lock (_sync)
        {
            return !_disposed && revision == _revision;
        }
    }
}
