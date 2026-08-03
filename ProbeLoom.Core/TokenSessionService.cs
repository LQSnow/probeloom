namespace ProbeLoom.Core;

public sealed class TokenSessionService
{
    private readonly ISecureValueStore _secureValueStore;
    private readonly HttpRequestExecutor _httpExecutor;
    private readonly RequestHistory _requestHistory;
    private readonly TokenSessionStore _tokenSessionStore;

    public TokenSessionService(
        ISecureValueStore secureValueStore,
        HttpRequestExecutor httpExecutor,
        RequestHistory requestHistory)
    {
        ArgumentNullException.ThrowIfNull(secureValueStore);
        ArgumentNullException.ThrowIfNull(httpExecutor);
        ArgumentNullException.ThrowIfNull(requestHistory);
        _secureValueStore = secureValueStore;
        _httpExecutor = httpExecutor;
        _requestHistory = requestHistory;
        _tokenSessionStore = new TokenSessionStore(secureValueStore);
    }

    public Task<TokenSession?> LoadAsync(
        Guid projectId,
        Guid environmentId,
        CancellationToken cancellationToken = default) =>
        _tokenSessionStore.LoadAsync(projectId, environmentId, cancellationToken);

    public Task SaveAsync(
        Guid projectId,
        Guid environmentId,
        TokenSession session,
        CancellationToken cancellationToken = default) =>
        _tokenSessionStore.SaveAsync(projectId, environmentId, session, cancellationToken);

    public Task ClearAsync(
        Guid projectId,
        Guid environmentId,
        CancellationToken cancellationToken = default) =>
        _tokenSessionStore.ClearAsync(projectId, environmentId, cancellationToken);

    public async Task<TokenRefreshResult> RefreshAsync(
        ProjectDocument project,
        ProjectEnvironment environment,
        TokenSession? currentSession,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var result = await TokenRefreshService.RefreshAsync(
            project,
            environment,
            _secureValueStore,
            currentSession,
            _httpExecutor,
            timeout,
            cancellationToken).ConfigureAwait(false);

        if (result.Succeeded && result.Execution is not null)
        {
            _requestHistory.Add(result.Execution);
        }

        return result;
    }
}
