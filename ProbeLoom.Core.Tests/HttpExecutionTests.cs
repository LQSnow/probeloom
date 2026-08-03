namespace ProbeLoom.Core.Tests;

internal static partial class CoreTests
{
    static async Task ExecutesHttpRequest()
    {
        string? capturedMethod = null;
        string? capturedUrl = null;
        string? capturedBody = null;
        string? capturedHeader = null;
        using var executor = new HttpRequestExecutor(new StubHandler(async (request, _) =>
        {
            capturedMethod = request.Method.Method;
            capturedUrl = request.RequestUri?.AbsoluteUri;
            capturedBody = request.Content is null ? null : await request.Content.ReadAsStringAsync();
            capturedHeader = request.Headers.GetValues("X-Trace").Single();
            var response = new HttpResponseMessage(HttpStatusCode.Created)
            {
                ReasonPhrase = "Created",
                Content = new StringContent("""{"ok":true,"id":42}""", Encoding.UTF8, "application/json")
            };
            response.Headers.Add("X-Server", "ProbeLoom.Tests");
            return response;
        }));
        var (_, _, node, plan) = CreateHttpPlan();
        node.Request!.Headers.Add(new RequestField("X-Trace", "abc"));
        node.Request.RawJsonBody = """{"name":"Ada"}""";
        var rebuilt = CreatePlanFor(node, plan.Uri.GetLeftPart(UriPartial.Authority));

        var result = await executor.ExecuteAsync(rebuilt);
        Equal("POST", capturedMethod);
        Equal(rebuilt.Uri.AbsoluteUri, capturedUrl);
        Equal("""{"name":"Ada"}""", capturedBody);
        Equal("abc", capturedHeader);
        Equal(HttpExecutionState.Succeeded, result.State);
        Equal(201, result.StatusCode);
        Equal(HttpResponseContentKind.Json, result.ContentKind);
        True(result.DisplayBody.Contains(Environment.NewLine), "JSON response was not formatted.");
        True(result.ResponseHeaders.Any(header => header.Name == "X-Server"), "Response headers were not captured.");
        True(result.Timing.HeadersReceived is not null, "Headers timing was not captured.");
        True(result.Timing.FirstByte is not null, "First-byte timing was not captured.");
    }

    static async Task BuildsSafeRequestSnapshot()
    {
        var (project, environment, node, _) = CreateHttpPlan();
        node.Request!.RawJsonBody = """{"note":"it's {{api.secret}}"}""";
        node.Request.Headers.Add(new RequestField("X-Trace", "visible"));
        node.Request.Headers.Add(new RequestField("Cookie", "session=do-not-show"));
        node.Request.Authentication.Kind = AuthenticationKind.BearerToken;
        node.Request.Authentication.BearerToken = "{{api.secret}}";
        var secret = new VariableDefinition { Name = "api.secret", IsSecret = true };
        node.Variables.Add(secret);
        var store = new InMemorySecureValueStore();
        await store.SetAsync(SecureValueKeys.Variable(project.Id, secret.Id), "real-secret");

        var prepared = await RequestPreparationService.PrepareAsync(
            project, environment, node, TimeSpan.FromSeconds(12), store, null);
        var snapshot = FinalRequestSnapshotFactory.Create(prepared)!;
        Equal("POST", snapshot.Method);
        Equal("••••••", snapshot.Headers.Single(header => header.Name == "Authorization").Value);
        Equal("••••••", snapshot.Headers.Single(header => header.Name == "Cookie").Value);
        True(
            snapshot.Headers.Single(header => header.Name == "Authorization").Source.Contains("Authentication"),
            "Authentication header source was not reported.");
        True(
            snapshot.Headers.Single(header => header.Name == "Cookie").Source.Contains("Request Header"),
            "Request header source was not reported.");
        True(!snapshot.Body.Contains("real-secret"), "Snapshot body leaked a secret.");
        Equal(Encoding.UTF8.GetByteCount(prepared.Plan!.Body), (int)snapshot.ContentLength);
        var curl = PowerShellCurlExporter.Export(snapshot);
        True(curl.Succeeded, curl.Error ?? "curl export failed.");
        True(curl.Command.StartsWith("curl.exe --request 'POST'"), "PowerShell curl method is missing.");
        True(curl.Command.Contains("it''s"), "PowerShell single quote was not escaped.");
        True(!curl.Command.Contains("real-secret"), "curl command leaked a secret.");
    }

    static async Task RecordsRedirectChain()
    {
        var requests = new List<(string Method, string Url)>();
        using var executor = new HttpRequestExecutor(new StubHandler((request, _) =>
        {
            requests.Add((request.Method.Method, request.RequestUri!.AbsoluteUri));
            return Task.FromResult(requests.Count switch
            {
                1 => Redirect(HttpStatusCode.Redirect, "/step-two"),
                2 => Redirect(HttpStatusCode.RedirectMethod, "/final"),
                _ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("done", Encoding.UTF8, "text/plain")
                }
            });
        }));
        var (_, _, _, plan) = CreateHttpPlan();
        var result = await executor.ExecuteAsync(plan);
        Equal(HttpExecutionState.Succeeded, result.State);
        Equal(2, result.RedirectChain.Count);
        Equal("POST", requests[0].Method);
        Equal("GET", requests[1].Method);
        Equal("GET", requests[2].Method);
        True(result.FinalUrl.EndsWith("/final"), "Final redirect URL was not captured.");
        True(result.RedirectChain.All(hop => hop.Duration >= TimeSpan.Zero), "Redirect timing is invalid.");
    }

    static async Task DetectsRedirectFailures()
    {
        using var loopExecutor = new HttpRequestExecutor(new StubHandler((request, _) =>
            Task.FromResult(Redirect(HttpStatusCode.Redirect, request.RequestUri!.AbsolutePath))));
        var (_, _, _, plan) = CreateHttpPlan();
        var loop = await loopExecutor.ExecuteAsync(plan);
        Equal(HttpErrorKind.Redirect, loop.ErrorKind);
        True(loop.ErrorTitle.Contains("循环"), "Redirect loop was not identified.");

        var count = 0;
        using var limitExecutor = new HttpRequestExecutor(
            new StubHandler((_, _) =>
                Task.FromResult(Redirect(HttpStatusCode.TemporaryRedirect, $"/hop/{++count}"))),
            maximumRedirects: 1);
        var limit = await limitExecutor.ExecuteAsync(plan);
        Equal(HttpErrorKind.Redirect, limit.ErrorKind);
        True(limit.ErrorTitle.Contains("次数"), "Redirect limit was not identified.");
    }

    static async Task ProtectsCrossHostRedirects()
    {
        var calls = 0;
        var leaked = false;
        using var executor = new HttpRequestExecutor(new StubHandler((request, _) =>
        {
            calls++;
            if (calls == 1)
            {
                return Task.FromResult(Redirect(
                    HttpStatusCode.TemporaryRedirect,
                    "https://other.example.test/final"));
            }
            leaked = request.Headers.Contains("Authorization") ||
                     request.Headers.Contains("Cookie") ||
                     request.Headers.Contains("X-API-Key");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }));
        var (_, _, _, plan) = CreateHttpPlan();
        plan = plan with
        {
            Headers =
            [
                new HttpHeaderValue("Authorization", "Bearer secret"),
                new HttpHeaderValue("Cookie", "session=secret"),
                new HttpHeaderValue("X-API-Key", "secret"),
                new HttpHeaderValue("Accept", "application/json")
            ]
        };
        var result = await executor.ExecuteAsync(plan);
        True(!leaked, "Sensitive headers crossed authority boundaries.");
        True(result.RedirectChain.Single().SensitiveHeadersRemoved, "Cross-host stripping was not reported.");
    }

    static HttpResponseMessage Redirect(HttpStatusCode statusCode, string location)
    {
        var response = new HttpResponseMessage(statusCode);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    static void FormatsResponseKinds()
    {
        Equal(HttpResponseContentKind.Empty, ResponseBodyFormatter.Format([], null, null).Kind);
        Equal(
            HttpResponseContentKind.Json,
            ResponseBodyFormatter.Format(Encoding.UTF8.GetBytes("""{"ok":true}"""), "application/problem+json", "utf-8").Kind);
        Equal(
            HttpResponseContentKind.Html,
            ResponseBodyFormatter.Format(Encoding.UTF8.GetBytes("<html></html>"), "text/html", "utf-8").Kind);
        Equal(
            HttpResponseContentKind.Text,
            ResponseBodyFormatter.Format(Encoding.UTF8.GetBytes("plain text"), "text/plain", "utf-8").Kind);
        Equal(
            HttpResponseContentKind.Binary,
            ResponseBodyFormatter.Format([0, 1, 2, 3, 255], "application/octet-stream", null).Kind);
    }

    static async Task TruncatesLargeResponse()
    {
        using var executor = new HttpRequestExecutor(
            new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new string('x', 256), Encoding.UTF8, "text/plain")
            })),
            maximumBodyBytes: 32);
        var (_, _, _, plan) = CreateHttpPlan();
        var result = await executor.ExecuteAsync(plan);
        True(result.IsBodyTruncated, "Large response should be marked as truncated.");
        Equal(32, result.RawBody.Length);
        Equal(256L, result.ResponseSizeBytes);
    }

    static async Task ClassifiesNetworkFailures()
    {
        var (_, _, _, plan) = CreateHttpPlan();
        using var tlsExecutor = new HttpRequestExecutor(new StubHandler((_, _) =>
            throw new HttpRequestException(HttpRequestError.SecureConnectionError, "certificate rejected")));
        var tls = await tlsExecutor.ExecuteAsync(plan);
        Equal(HttpErrorKind.Tls, tls.ErrorKind);

        using var connectionExecutor = new HttpRequestExecutor(new StubHandler((_, _) =>
            throw new HttpRequestException(HttpRequestError.ConnectionError, "connection refused")));
        var connection = await connectionExecutor.ExecuteAsync(plan);
        Equal(HttpErrorKind.Connection, connection.ErrorKind);
    }

    static async Task DistinguishesTimeoutAndCancellation()
    {
        var (_, _, _, plan) = CreateHttpPlan();
        using var timeoutExecutor = new HttpRequestExecutor(new StubHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        var timedOut = await timeoutExecutor.ExecuteAsync(plan with { Timeout = TimeSpan.FromMilliseconds(30) });
        Equal(HttpExecutionState.TimedOut, timedOut.State);
        Equal(HttpErrorKind.Timeout, timedOut.ErrorKind);

        using var cancelExecutor = new HttpRequestExecutor(new StubHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        using var cancellation = new CancellationTokenSource(30);
        var cancelled = await cancelExecutor.ExecuteAsync(plan with { Timeout = TimeSpan.FromSeconds(5) }, cancellation.Token);
        Equal(HttpExecutionState.Cancelled, cancelled.State);
        Equal(HttpErrorKind.Cancelled, cancelled.ErrorKind);
    }

    static async Task MasksSensitiveRequestData()
    {
        var project = ProjectOperations.CreateProject("Security");
        var environment = project.Environments[0];
        environment.BaseUrl = "https://example.test";
        var group = ProjectOperations.AddGroup(project, null, "Auth").Value!;
        var node = ProjectOperations.AddEndpoint(project, group.Id, "Login").Value!;
        node.Request!.Method = "POST";
        node.Request.Route = "/login";
        node.Request.RawJsonBody = """{"username":"ada","password":"open-sesame","nested":{"accessToken":"abc"}}""";
        node.Request.Headers.Add(new RequestField("X-Trace", "trace-42"));
        node.Request.Authentication.Kind = AuthenticationKind.ApiKey;
        node.Request.Authentication.ApiKeyLocation = ApiKeyLocation.Header;
        node.Request.Authentication.ApiKeyName = "X-API-Key";
        node.Request.Authentication.ApiKeyValue = "{{auth.key}}";
        var secret = new VariableDefinition { Name = "auth.key", IsSecret = true };
        project.Variables.Add(secret);
        var store = new InMemorySecureValueStore();
        await store.SetAsync(SecureValueKeys.Variable(project.Id, secret.Id), "real-key");

        var prepared = await RequestPreparationService.PrepareAsync(
            project, environment, node, TimeSpan.FromSeconds(5), store, null);
        var snapshot = FinalRequestSnapshotFactory.Create(prepared)!;
        True(!snapshot.Body.Contains("open-sesame", StringComparison.Ordinal), "Password leaked from JSON body.");
        True(!snapshot.Body.Contains("\"abc\"", StringComparison.Ordinal), "Token leaked from nested JSON body.");
        Equal("Request Header", snapshot.Headers.Single(header => header.Name == "X-Trace").Source);
        True(
            snapshot.Headers.Single(header => header.Name == "X-API-Key").Source.Contains("Authentication"),
            "Injected API key header source was not attributed.");
    }

    static (ProjectDocument Project, ProjectEnvironment Environment, ProjectNode Node, HttpRequestPlan Plan) CreateHttpPlan()
    {
        var project = ProjectOperations.CreateProject("HTTP");
        var environment = project.Environments[0];
        var group = ProjectOperations.AddGroup(project, null, "Requests").Value!;
        var node = ProjectOperations.AddEndpoint(project, group.Id, "Create").Value!;
        node.Request!.Method = "POST";
        node.Request.Route = "/items";
        var plan = HttpRequestPlanner.Create(project, environment, node, TimeSpan.FromSeconds(2)).Plan!;
        return (project, environment, node, plan);
    }

    static HttpRequestPlan CreatePlanFor(ProjectNode node, string baseUrl)
    {
        var project = new ProjectDocument { Name = "HTTP" };
        var environment = new ProjectEnvironment { Name = "Test", BaseUrl = baseUrl };
        var group = new ProjectNode { Kind = ProjectNodeKind.Group, Name = "Requests", Children = [node] };
        project.Items.Add(group);
        return HttpRequestPlanner.Create(project, environment, node, TimeSpan.FromSeconds(2)).Plan!;
    }

}
