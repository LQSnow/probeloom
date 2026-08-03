namespace ProbeLoom.Core;

public sealed record RequestExecutionPreparation(
    PreparedRequestResult Prepared,
    TokenSession? TokenSession,
    TokenRefreshResult? TokenRefresh,
    string? TokenRefreshInfrastructureError,
    bool TokenSessionChanged);

public sealed record RequestExecutionOutcome(
    HttpExecutionResult Execution,
    TokenSession? TokenSession,
    TokenExtractionResult? TokenCapture,
    string? TokenCapturePersistenceError,
    bool TokenSessionChanged);

public sealed class RequestExecutionService
{
    private readonly ISecureValueStore _secureValueStore;
    private readonly HttpRequestExecutor _httpExecutor;
    private readonly RequestHistory _requestHistory;
    private readonly TokenSessionService _tokenSessionService;

    public RequestExecutionService(
        ISecureValueStore secureValueStore,
        HttpRequestExecutor httpExecutor,
        RequestHistory requestHistory)
        : this(
            secureValueStore,
            httpExecutor,
            requestHistory,
            new TokenSessionService(secureValueStore, httpExecutor, requestHistory))
    {
    }

    public RequestExecutionService(
        ISecureValueStore secureValueStore,
        HttpRequestExecutor httpExecutor,
        RequestHistory requestHistory,
        TokenSessionService tokenSessionService)
    {
        ArgumentNullException.ThrowIfNull(secureValueStore);
        ArgumentNullException.ThrowIfNull(httpExecutor);
        ArgumentNullException.ThrowIfNull(requestHistory);
        ArgumentNullException.ThrowIfNull(tokenSessionService);
        _secureValueStore = secureValueStore;
        _httpExecutor = httpExecutor;
        _requestHistory = requestHistory;
        _tokenSessionService = tokenSessionService;
    }

    public async Task<RequestExecutionPreparation> PrepareAsync(
        ProjectDocument project,
        ProjectEnvironment? environment,
        ProjectNode node,
        TimeSpan timeout,
        TokenSession? tokenSession,
        CancellationToken cancellationToken = default)
    {
        var effectiveSession = tokenSession;
        var sessionChanged = false;
        var prepared = await RequestPreparationService.PrepareAsync(
            project,
            environment,
            node,
            timeout,
            _secureValueStore,
            effectiveSession,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        TokenRefreshResult? refresh = null;
        string? refreshInfrastructureError = null;
        if (prepared.RequiresTokenRefresh && environment is not null)
        {
            try
            {
                refresh = await _tokenSessionService.RefreshAsync(
                    project,
                    environment,
                    effectiveSession,
                    timeout,
                    cancellationToken).ConfigureAwait(false);
                if (refresh.Succeeded && refresh.Session is not null)
                {
                    effectiveSession = refresh.Session;
                    sessionChanged = true;

                    prepared = await RequestPreparationService.PrepareAsync(
                        project,
                        environment,
                        node,
                        timeout,
                        _secureValueStore,
                        effectiveSession,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (SecureValueStoreException exception)
            {
                refreshInfrastructureError = exception.Message;
            }
        }

        return new RequestExecutionPreparation(
            prepared,
            effectiveSession,
            refresh,
            refreshInfrastructureError,
            sessionChanged);
    }

    public async Task<RequestExecutionOutcome> ExecuteAsync(
        ProjectDocument project,
        ProjectEnvironment? environment,
        ProjectNode node,
        RequestExecutionPreparation preparation,
        CancellationToken cancellationToken = default)
    {
        if (!preparation.Prepared.Succeeded || preparation.Prepared.Plan is null)
        {
            throw new InvalidOperationException("请求尚未通过校验，不能执行。");
        }

        var execution = await _httpExecutor.ExecuteAsync(
            preparation.Prepared.Plan,
            cancellationToken).ConfigureAwait(false);
        _requestHistory.Add(execution);

        var effectiveSession = preparation.TokenSession;
        var sessionChanged = false;
        TokenExtractionResult? capture = null;
        string? capturePersistenceError = null;
        if (execution.State == HttpExecutionState.Succeeded &&
            node.Request!.TokenCapture.IsEnabled &&
            environment is not null)
        {
            capture = TokenExtractor.Extract(
                execution.RawBody,
                node.Request.TokenCapture,
                effectiveSession);
            if (capture.Succeeded && capture.Session is not null)
            {
                try
                {
                    await _tokenSessionService.SaveAsync(
                        project.Id,
                        environment.Id,
                        capture.Session,
                        cancellationToken).ConfigureAwait(false);
                    effectiveSession = capture.Session;
                    sessionChanged = true;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (SecureValueStoreException exception)
                {
                    effectiveSession = null;
                    sessionChanged = true;
                    capturePersistenceError = exception.Message;
                }
            }
        }

        return new RequestExecutionOutcome(
            execution,
            effectiveSession,
            capture,
            capturePersistenceError,
            sessionChanged);
    }
}
