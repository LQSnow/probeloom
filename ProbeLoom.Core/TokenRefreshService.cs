namespace ProbeLoom.Core;

public sealed record TokenRefreshResult(
    bool Succeeded,
    TokenSession? Session,
    HttpExecutionResult? Execution,
    string? Error);

public static class TokenRefreshService
{
    public static async Task<TokenRefreshResult> RefreshAsync(
        ProjectDocument project,
        ProjectEnvironment environment,
        ISecureValueStore secureValueStore,
        TokenSession? currentSession,
        HttpRequestExecutor executor,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (project.RefreshRequestNodeId is not Guid refreshRequestId ||
            ProjectOperations.FindNode(project, refreshRequestId) is not { Request: not null } refreshNode)
        {
            return new TokenRefreshResult(
                false,
                currentSession,
                null,
                "尚未配置 Refresh 请求。");
        }

        var prepared = await RequestPreparationService.PrepareAsync(
            project,
            environment,
            refreshNode,
            timeout,
            secureValueStore,
            currentSession,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!prepared.Succeeded || prepared.Plan is null)
        {
            return new TokenRefreshResult(
                false,
                currentSession,
                null,
                string.Join(" ", prepared.Validation.Errors));
        }

        var execution = await executor.ExecuteAsync(prepared.Plan, cancellationToken).ConfigureAwait(false);
        if (execution.State != HttpExecutionState.Succeeded || !execution.IsSuccessStatusCode)
        {
            var error = execution.State == HttpExecutionState.Succeeded
                ? $"Refresh 请求返回 HTTP {execution.StatusCode}。"
                : execution.ErrorTitle;
            return new TokenRefreshResult(false, currentSession, execution, error);
        }

        var extraction = TokenExtractor.Extract(
            execution.RawBody,
            refreshNode.Request!.TokenCapture,
            currentSession);
        if (!extraction.Succeeded || extraction.Session is null)
        {
            return new TokenRefreshResult(false, currentSession, execution, extraction.Error);
        }

        await new TokenSessionStore(secureValueStore).SaveAsync(
            project.Id,
            environment.Id,
            extraction.Session,
            cancellationToken).ConfigureAwait(false);
        return new TokenRefreshResult(true, extraction.Session, execution, null);
    }
}
