namespace ProbeLoom.Core.Tests;

internal static partial class CoreTests
{
    static void ExtractsTokens()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var configuration = new TokenCaptureConfiguration
        {
            IsEnabled = true,
            AccessTokenPath = "$.tokens[0].access",
            RefreshTokenPath = "$.tokens[0].refresh",
            ExpiresInPath = "$.expiresIn"
        };
        var result = TokenExtractor.Extract(
            """{"tokens":[{"access":"access","refresh":"refresh"}],"expiresIn":60}""",
            configuration,
            now: now);
        True(result.Succeeded, result.Error ?? "Token extraction failed.");
        Equal("access", result.Session!.AccessToken);
        Equal("refresh", result.Session.RefreshToken);
        Equal(now.AddSeconds(60), result.Session.ExpiresAt);
        True(result.Session.IsExpired(now.AddSeconds(50)), "Clock skew should mark the token expired.");
    }

    static async Task RefreshesTokens()
    {
        var project = ProjectOperations.CreateProject("Refresh");
        var environment = project.Environments[0];
        var group = ProjectOperations.AddGroup(project, null, "Auth").Value!;
        var refreshNode = ProjectOperations.AddEndpoint(project, group.Id, "Refresh").Value!;
        refreshNode.Request!.Method = "POST";
        refreshNode.Request.Route = "/refresh";
        refreshNode.Request.RawJsonBody = """{"refresh":"{{token.refresh}}"}""";
        refreshNode.Request.TokenCapture.IsEnabled = true;
        project.RefreshRequestNodeId = refreshNode.Id;
        var oldSession = new TokenSession("old-access", "old-refresh", DateTimeOffset.Now.AddMinutes(-1), DateTimeOffset.Now);
        var secureStore = new InMemorySecureValueStore();
        using var successExecutor = new HttpRequestExecutor(new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"accessToken":"new-access","refreshToken":"new-refresh","expiresIn":120}""",
                    Encoding.UTF8,
                    "application/json")
            })));
        var success = await TokenRefreshService.RefreshAsync(
            project,
            environment,
            secureStore,
            oldSession,
            successExecutor,
            TimeSpan.FromSeconds(2));
        True(success.Succeeded, success.Error ?? "Refresh should succeed.");
        Equal("new-access", success.Session!.AccessToken);
        Equal(
            "new-access",
            (await new TokenSessionStore(secureStore).LoadAsync(project.Id, environment.Id))!.AccessToken);

        using var failureExecutor = new HttpRequestExecutor(new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized))));
        var failure = await TokenRefreshService.RefreshAsync(
            project,
            environment,
            secureStore,
            success.Session,
            failureExecutor,
            TimeSpan.FromSeconds(2));
        True(!failure.Succeeded, "Refresh failure should be reported.");
        Equal("new-access", failure.Session!.AccessToken);
        Equal(
            "new-access",
            (await new TokenSessionStore(secureStore).LoadAsync(project.Id, environment.Id))!.AccessToken);
    }

    static async Task CoordinatesTokenSessions()
    {
        var project = ProjectOperations.CreateProject("Token Session");
        var environment = project.Environments[0];
        environment.BaseUrl = "https://api.example.test";
        var group = ProjectOperations.AddGroup(project, null, "Auth").Value!;
        var refreshNode = ProjectOperations.AddEndpoint(project, group.Id, "Refresh").Value!;
        refreshNode.Request!.Method = "POST";
        refreshNode.Request.Route = "/refresh";
        refreshNode.Request.TokenCapture.IsEnabled = true;
        project.RefreshRequestNodeId = refreshNode.Id;

        var secureStore = new InMemorySecureValueStore();
        var history = new RequestHistory();
        using var executor = new HttpRequestExecutor(new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"accessToken":"refreshed-access","refreshToken":"refreshed-refresh","expiresIn":120}""",
                    Encoding.UTF8,
                    "application/json")
            })));
        var service = new TokenSessionService(secureStore, executor, history);
        var initial = new TokenSession(
            "initial-access",
            "initial-refresh",
            DateTimeOffset.Now.AddMinutes(-1),
            DateTimeOffset.Now.AddMinutes(-5));

        await service.SaveAsync(project.Id, environment.Id, initial);
        Equal(
            "initial-access",
            (await service.LoadAsync(project.Id, environment.Id))!.AccessToken);

        var refreshed = await service.RefreshAsync(
            project,
            environment,
            initial,
            TimeSpan.FromSeconds(2));
        True(refreshed.Succeeded, refreshed.Error ?? "Token refresh should succeed.");
        Equal("refreshed-access", refreshed.Session!.AccessToken);
        Equal(1, history.Entries.Count);
        Equal(refreshNode.Id, history.Entries[0].RequestNodeId);
        Equal(
            "refreshed-access",
            (await service.LoadAsync(project.Id, environment.Id))!.AccessToken);

        await service.ClearAsync(project.Id, environment.Id);
        True(
            await service.LoadAsync(project.Id, environment.Id) is null,
            "Clearing the token session should remove the persisted value.");
    }

    static async Task OrchestratesRequestExecution()
    {
        var project = ProjectOperations.CreateProject("Execution");
        var environment = project.Environments[0];
        environment.BaseUrl = "https://api.example.test";
        var group = ProjectOperations.AddGroup(project, null, "Auth").Value!;
        var refreshNode = ProjectOperations.AddEndpoint(project, group.Id, "Refresh").Value!;
        refreshNode.Request!.Method = "POST";
        refreshNode.Request.Route = "/refresh";
        refreshNode.Request.RawJsonBody = """{"refresh":"{{token.refresh}}"}""";
        refreshNode.Request.TokenCapture.IsEnabled = true;
        project.RefreshRequestNodeId = refreshNode.Id;

        var requestNode = ProjectOperations.AddEndpoint(project, group.Id, "Protected").Value!;
        requestNode.Request!.Route = "/protected";
        requestNode.Request.Authentication.Kind = AuthenticationKind.BearerToken;
        requestNode.Request.TokenCapture.IsEnabled = true;

        var oldSession = new TokenSession(
            "old-access",
            "old-refresh",
            DateTimeOffset.Now.AddMinutes(-5),
            DateTimeOffset.Now.AddMinutes(-10));
        string? protectedAuthorization = null;
        using var executor = new HttpRequestExecutor(new StubHandler(async (request, _) =>
        {
            if (request.RequestUri?.AbsolutePath == "/refresh")
            {
                var body = request.Content is null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync();
                True(body.Contains("old-refresh"), "Refresh request did not use the previous refresh token.");
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"accessToken":"refreshed-access","refreshToken":"refreshed-refresh","expiresIn":120}""",
                        Encoding.UTF8,
                        "application/json")
                };
            }

            protectedAuthorization = request.Headers.Authorization?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"accessToken":"captured-access","refreshToken":"captured-refresh","expiresIn":300}""",
                    Encoding.UTF8,
                    "application/json")
            };
        }));
        var secureStore = new InMemorySecureValueStore();
        var history = new RequestHistory();
        var service = new RequestExecutionService(secureStore, executor, history);

        var preparation = await service.PrepareAsync(
            project,
            environment,
            requestNode,
            TimeSpan.FromSeconds(2),
            oldSession);
        True(preparation.Prepared.Succeeded, string.Join(" ", preparation.Prepared.Validation.Errors));
        True(preparation.TokenRefresh?.Succeeded == true, "Expired token should be refreshed before execution.");
        True(preparation.TokenSessionChanged, "Successful refresh should update the effective token session.");
        Equal("refreshed-access", preparation.TokenSession!.AccessToken);

        var outcome = await service.ExecuteAsync(
            project,
            environment,
            requestNode,
            preparation);
        Equal(HttpExecutionState.Succeeded, outcome.Execution.State);
        Equal("Bearer refreshed-access", protectedAuthorization);
        True(outcome.TokenCapture?.Succeeded == true, "Response token capture should succeed.");
        True(outcome.TokenSessionChanged, "Token capture should update the effective token session.");
        Equal("captured-access", outcome.TokenSession!.AccessToken);
        Equal(2, history.Entries.Count);
        Equal(requestNode.Id, history.Entries[0].RequestNodeId);
        Equal(refreshNode.Id, history.Entries[1].RequestNodeId);
        Equal(
            "captured-access",
            (await new TokenSessionStore(secureStore).LoadAsync(project.Id, environment.Id))!.AccessToken);
    }

}
