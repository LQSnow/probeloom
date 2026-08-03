namespace ProbeLoom.Core.Tests;

internal static partial class CoreTests
{
    static async Task ResolvesVariableInheritance()
    {
        var project = ProjectOperations.CreateProject("Variables");
        project.Variables.Add(new VariableDefinition { Name = "host", Value = "project.test" });
        project.Variables.Add(new VariableDefinition { Name = "tenant", Value = "project" });
        var environment = project.Environments[0];
        environment.Variables.Add(new VariableDefinition { Name = "host", Value = "environment.test" });
        var group = ProjectOperations.AddGroup(project, null, "Group").Value!;
        group.Variables.Add(new VariableDefinition { Name = "tenant", Value = "group" });
        var endpoint = ProjectOperations.AddEndpoint(project, group.Id, "Endpoint").Value!;
        endpoint.Variables.Add(new VariableDefinition { Name = "resource", Value = "{{tenant}}/users" });
        var requestCase = ProjectOperations.AddRequestCase(project, endpoint.Id, "Case").Value!;
        requestCase.Variables.Add(new VariableDefinition { Name = "tenant", Value = "case" });

        var result = await VariableResolver.ResolveAsync(
            project,
            environment,
            requestCase,
            new InMemorySecureValueStore());
        True(result.Succeeded, string.Join(" ", result.Issues.Select(issue => issue.Message)));
        Equal("environment.test", result.Variables["host"].Value);
        Equal("case/users", result.Variables["resource"].Value);
        Equal(1, result.Variables["host"].OverriddenSources.Count);
        Equal(2, result.Variables["tenant"].OverriddenSources.Count);
        Equal(VariableScopeKind.RequestCase, result.Variables["tenant"].Source.Scope);
    }

    static async Task DetectsVariableFailures()
    {
        var project = ProjectOperations.CreateProject("Variables");
        var group = ProjectOperations.AddGroup(project, null, "Group").Value!;
        var endpoint = ProjectOperations.AddEndpoint(project, group.Id, "Endpoint").Value!;
        endpoint.Variables.Add(new VariableDefinition { Name = "a", Value = "{{b}}" });
        endpoint.Variables.Add(new VariableDefinition { Name = "b", Value = "{{a}}" });
        endpoint.Request!.Route = "/{{missing}}";

        var resolution = await VariableResolver.ResolveAsync(
            project,
            project.Environments[0],
            endpoint,
            new InMemorySecureValueStore());
        True(
            resolution.Issues.Any(issue => issue.Kind == VariableIssueKind.CircularReference),
            "Circular reference was not reported.");
        var replacement = resolution.Replace(endpoint.Request.Route, "Route");
        True(
            replacement.Issues.Any(issue => issue.Kind == VariableIssueKind.Missing),
            "Missing variable was not reported.");
    }

    static async Task ProtectsSecrets()
    {
        await WithTemporaryDirectory(async directory =>
        {
            var project = ProjectOperations.CreateProject("Secrets");
            var secret = new VariableDefinition { Name = "api.key", IsSecret = true };
            project.Variables.Add(secret);
            var group = ProjectOperations.AddGroup(project, null, "Auth").Value!;
            var endpoint = ProjectOperations.AddEndpoint(project, group.Id, "Protected").Value!;
            endpoint.Request!.Route = "/protected";
            endpoint.Request.Authentication.Kind = AuthenticationKind.BearerToken;
            endpoint.Request.Authentication.BearerToken = "{{api.key}}";
            var secureStore = new InMemorySecureValueStore();
            await secureStore.SetAsync(SecureValueKeys.Variable(project.Id, secret.Id), "never-serialize-me");
            await secureStore.SetAsync(
                SecureValueKeys.TokenSession(project.Id, project.Environments[0].Id),
                "isolated-token");

            var path = Path.Combine(directory, "secrets.probeloom.json");
            await new ProjectFileStore().SaveAsync(path, project);
            var json = await File.ReadAllTextAsync(path);
            True(!json.Contains("never-serialize-me"), "Secret leaked into the project file.");
            True(!json.Contains("isolated-token"), "Token session leaked into the project file.");
            var loaded = await new ProjectFileStore().LoadAsync(path);
            var loadedSecret = loaded.Variables.Single();
            True(loadedSecret.IsSecret, "Secret metadata was not restored.");
            Equal(string.Empty, loadedSecret.Value);
            Equal(
                "{{api.key}}",
                ProjectOperations.EnumerateNodes(loaded.Items)
                    .Single(node => node.Kind == ProjectNodeKind.Endpoint)
                    .Request!.Authentication.BearerToken);
            Equal(
                "never-serialize-me",
                await secureStore.GetAsync(SecureValueKeys.Variable(project.Id, secret.Id)));
            True(
                await secureStore.GetAsync(
                    SecureValueKeys.TokenSession(project.Id, Guid.NewGuid())) is null,
                "Token session was not isolated by environment.");
        });
    }

    static async Task RetriesSecureStorageLoadAfterFailure()
    {
        var loadAttempts = 0;
        var store = new TransactionalSecureValueStore(
            cancellationToken =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                loadAttempts++;
                if (loadAttempts == 1)
                {
                    return Task.FromException<IReadOnlyDictionary<string, string>>(
                        new IOException("controlled load failure"));
                }

                return Task.FromResult<IReadOnlyDictionary<string, string>>(
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["token"] = "persisted"
                    });
            },
            (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            });

        await ThrowsAsync<IOException>(() => store.GetAsync("token"));
        Equal("persisted", await store.GetAsync("token"));
        Equal(2, loadAttempts);
    }

    static async Task PreservesSecureStorageStateAfterSaveFailures()
    {
        var failWrites = true;
        IReadOnlyDictionary<string, string> persistedValues =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["token"] = "old"
            };
        var store = new TransactionalSecureValueStore(
            cancellationToken =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult<IReadOnlyDictionary<string, string>>(
                    new Dictionary<string, string>(persistedValues, StringComparer.Ordinal));
            },
            (values, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (failWrites)
                {
                    return Task.FromException(new IOException("controlled save failure"));
                }

                persistedValues = new Dictionary<string, string>(values, StringComparer.Ordinal);
                return Task.CompletedTask;
            });

        await ThrowsAsync<IOException>(() => store.SetAsync("token", "new"));
        Equal("old", await store.GetAsync("token"));
        Equal("old", persistedValues["token"]);

        failWrites = false;
        await store.SetAsync("token", "new");
        Equal("new", await store.GetAsync("token"));
        Equal("new", persistedValues["token"]);

        failWrites = true;
        await ThrowsAsync<IOException>(() => store.RemoveAsync("token"));
        Equal("new", await store.GetAsync("token"));
        Equal("new", persistedValues["token"]);

        failWrites = false;
        await store.RemoveAsync("token");
        True(await store.GetAsync("token") is null, "Removed secure value remains in memory.");
        True(!persistedValues.ContainsKey("token"), "Removed secure value remains in persisted state.");
    }

    static async Task PreparesVariablesAndAuthentication()
    {
        var project = ProjectOperations.CreateProject("Prepared");
        var environment = project.Environments[0];
        environment.BaseUrl = "https://{{host}}";
        environment.Variables.Add(new VariableDefinition { Name = "host", Value = "api.example.test" });
        var secret = new VariableDefinition { Name = "api.key", IsSecret = true };
        project.Variables.Add(secret);
        var group = ProjectOperations.AddGroup(project, null, "Group").Value!;
        group.RoutePrefix = "/{{tenant}}";
        group.IsRoutePrefixEnabled = true;
        group.Variables.Add(new VariableDefinition { Name = "tenant", Value = "acme" });
        var endpoint = ProjectOperations.AddEndpoint(project, group.Id, "Endpoint").Value!;
        endpoint.Request!.Route = "/users/{id}";
        endpoint.Request.PathParameters.Add(new RequestField("id", "{{user.id}}"));
        endpoint.Request.QueryParameters.Add(new RequestField("filter", "{{tenant}} users"));
        endpoint.Request.Headers.Add(new RequestField("X-Tenant", "{{tenant}}"));
        endpoint.Request.RawJsonBody = """{"tenant":"{{tenant}}"}""";
        endpoint.Variables.Add(new VariableDefinition { Name = "user.id", Value = "Ada Lovelace" });
        endpoint.Request.Authentication.Kind = AuthenticationKind.ApiKey;
        endpoint.Request.Authentication.ApiKeyName = "X-API-Key";
        endpoint.Request.Authentication.ApiKeyValue = "{{api.key}}";
        var secureStore = new InMemorySecureValueStore();
        await secureStore.SetAsync(SecureValueKeys.Variable(project.Id, secret.Id), "sensitive-value");

        var prepared = await RequestPreparationService.PrepareAsync(
            project,
            environment,
            endpoint,
            TimeSpan.FromSeconds(2),
            secureStore,
            null);
        True(prepared.Succeeded, string.Join(" ", prepared.Validation.Errors));
        Equal(
            "https://api.example.test/acme/users/Ada%20Lovelace?filter=acme%20users",
            prepared.Plan!.Uri.AbsoluteUri);
        True(
            prepared.Plan.Headers.Any(header =>
                header.Name == "X-API-Key" && header.Value == "sensitive-value"),
            "Structured API key was not injected.");
        True(!prepared.Plan.DisplayUrl.Contains("sensitive-value"), "Safe URL leaked a secret.");
        Equal("""{"tenant":"acme"}""", prepared.Plan.Body);

        endpoint.Request.Authentication.ApiKeyLocation = ApiKeyLocation.Query;
        var queryKey = await RequestPreparationService.PrepareAsync(
            project,
            environment,
            endpoint,
            TimeSpan.FromSeconds(2),
            secureStore,
            null);
        True(
            queryKey.Plan!.Uri.Query.Contains("X-API-Key=sensitive-value"),
            "Query API key was not injected.");
        True(
            queryKey.Plan.DisplayUrl.Contains("X-API-Key=%E2%80%A2%E2%80%A2%E2%80%A2%E2%80%A2%E2%80%A2%E2%80%A2"),
            "Query API key was not masked in the display URL.");
    }

    static async Task InjectsBearerAndBasicAuthentication()
    {
        var (project, environment, node, _) = CreateHttpPlan();
        var secureStore = new InMemorySecureValueStore();
        node.Request!.Authentication.Kind = AuthenticationKind.BearerToken;
        var bearer = await RequestPreparationService.PrepareAsync(
            project,
            environment,
            node,
            TimeSpan.FromSeconds(2),
            secureStore,
            new TokenSession("session-token", "refresh", null, DateTimeOffset.Now));
        True(bearer.Succeeded, string.Join(" ", bearer.Validation.Errors));
        True(
            bearer.Plan!.Headers.Any(header =>
                header.Name == "Authorization" && header.Value == "Bearer session-token"),
            "Bearer session token was not injected.");

        var password = new VariableDefinition { Name = "basic.password", IsSecret = true };
        node.Variables.Add(password);
        await secureStore.SetAsync(SecureValueKeys.Variable(project.Id, password.Id), "probe");
        node.Request.Authentication.Kind = AuthenticationKind.Basic;
        node.Request.Authentication.Username = "developer";
        node.Request.Authentication.Password = "{{basic.password}}";
        var basic = await RequestPreparationService.PrepareAsync(
            project,
            environment,
            node,
            TimeSpan.FromSeconds(2),
            secureStore,
            null);
        True(basic.Succeeded, string.Join(" ", basic.Validation.Errors));
        Equal(
            "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("developer:probe")),
            basic.Plan!.Headers.Single(header => header.Name == "Authorization").Value);
    }

}
