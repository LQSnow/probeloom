namespace ProbeLoom.Core.Tests;

internal static partial class CoreTests
{
    static async Task BuildsRouteCatalog()
    {
        var project = ProjectOperations.CreateProject("Catalog");
        project.Environments[0].BaseUrl = "https://example.test";
        project.RouteParts.Clear();
        project.RouteParts.Add(new RoutePart { Name = "API", Value = "/api" });
        var group = ProjectOperations.AddGroup(project, null, "Users").Value!;
        group.IsRoutePrefixEnabled = true;
        group.RoutePrefix = "/v1";
        var first = ProjectOperations.AddEndpoint(project, group.Id, "By id").Value!;
        first.Request!.Route = "/users/{id}";
        first.Request.PathParameters.Add(new RequestField("id", "user 42"));
        var second = ProjectOperations.AddEndpoint(project, group.Id, "By user id").Value!;
        second.Request!.Route = "/users/{userId}";
        second.Request.PathParameters.Add(new RequestField("userId", "user 42"));

        var catalog = await RouteCatalogBuilder.BuildAsync(
            project, project.Environments[0], new InMemorySecureValueStore());
        Equal(first.Id, catalog.Entries[0].NodeId);
        Equal("/api/v1/users/{id}", catalog.Entries[0].RouteTemplate);
        True(catalog.Entries[0].ExampleUrl.Contains("user%2042"), "Catalog did not use the resolved encoded URL.");
        True(
            catalog.Conflicts.Any(conflict => conflict.Kind == RouteConflictKind.ParameterizedDuplicate),
            "Parameterized conflict was not detected.");
    }

    static async Task DebouncesRouteCatalogRefreshes()
    {
        var project = CreateCatalogProjectWithSecret();
        var store = new CountingSecureValueStore("42");
        using var service = new RouteCatalogService(store, TimeSpan.FromMilliseconds(50));

        var first = service.RefreshAsync(
            project,
            project.Environments[0],
            null,
            RouteCatalogRefreshMode.Debounced);
        var second = service.RefreshAsync(
            project,
            project.Environments[0],
            null,
            RouteCatalogRefreshMode.Debounced);

        var firstResult = await first;
        True(firstResult is null, "Superseded debounced refresh should not publish a result.");
        var result = await second;
        True(result?.Catalog?.Entries.Count == 1, "Latest debounced refresh did not build the catalog.");
        Equal(1, store.GetCalls);
        True(service.IsCurrent(result!), "Latest catalog revision was not current.");
    }

    static async Task CancelsStaleRouteCatalogBuilds()
    {
        var project = CreateCatalogProjectWithSecret();
        var store = new BlockingFirstSecureValueStore("42");
        using var service = new RouteCatalogService(store, TimeSpan.Zero);

        var first = service.RefreshAsync(
            project,
            project.Environments[0],
            null,
            RouteCatalogRefreshMode.Immediate);
        await store.FirstReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var second = service.RefreshAsync(
            project,
            project.Environments[0],
            null,
            RouteCatalogRefreshMode.Immediate);

        var firstResult = await first;
        True(firstResult is null, "Cancelled catalog build should not publish a stale result.");
        var result = await second;
        True(result?.Catalog?.Entries.Count == 1, "Replacement catalog build did not complete.");
        Equal(2, store.GetCalls);
        True(service.IsCurrent(result!), "Replacement catalog revision was not current.");

        service.Cancel();
        True(!service.IsCurrent(result!), "Cancelled catalog revision remained current.");
    }

    static async Task GeneratesMarkdownDocumentation()
    {
        var project = CreateCompleteProject();
        project.Description = "A documented API.";
        project.Environments[0].BaseUrl = "https://example.test";
        var endpoint = ProjectOperations.EnumerateNodes(project.Items)
            .First(node => node.Kind == ProjectNodeKind.Endpoint);
        endpoint.Summary = "Create a user";
        endpoint.Tags = "users, write";
        endpoint.Request!.Headers.Add(new RequestField("Authorization", "Bearer should-not-leak"));
        endpoint.Request.RawJsonBody = """{"password":"should-not-leak","name":"Ada"}""";
        var catalog = await RouteCatalogBuilder.BuildAsync(
            project, project.Environments[0], new InMemorySecureValueStore());
        var markdown = ApiDocumentationMarkdownGenerator.Generate(catalog);
        True(markdown.Contains("## Contents"), "Markdown table of contents is missing.");
        True(markdown.Contains($"route-{endpoint.Id:N}"), "Stable endpoint anchor is missing.");
        True(!markdown.Contains("should-not-leak", StringComparison.Ordinal), "Sensitive value leaked into Markdown.");
        True(markdown.Contains(SensitiveDataMasker.Mask), "Masked marker is missing from Markdown.");
    }

    static async Task RoundTripsDocumentationMetadata()
    {
        await WithTemporaryDirectory(async directory =>
        {
            var project = CreateCompleteProject();
            project.Description = "Project docs";
            var endpoint = ProjectOperations.EnumerateNodes(project.Items)
                .First(node => node.Kind == ProjectNodeKind.Endpoint);
            endpoint.Summary = "Summary";
            endpoint.Description = "Details";
            endpoint.Tags = "one,two";
            endpoint.IsDeprecated = true;
            endpoint.Request!.QueryParameters[0].Description = "Send a notification";
            var path = Path.Combine(directory, "docs.probeloom.json");
            var store = new ProjectFileStore();
            await store.SaveAsync(path, project);
            var json = await File.ReadAllTextAsync(path);
            True(json.Contains("\"version\": 4"), "Project was not saved as v4.");
            var loaded = await store.LoadAsync(path);
            var loadedEndpoint = ProjectOperations.FindNode(loaded, endpoint.Id)!;
            Equal("Project docs", loaded.Description);
            Equal("Summary", loadedEndpoint.Summary);
            True(loadedEndpoint.IsDeprecated, "Deprecated metadata was lost.");
            Equal("Send a notification", loadedEndpoint.Request!.QueryParameters[0].Description);
        });
    }

    static async Task ReorderKeepsRepresentationsAligned()
    {
        var project = ProjectOperations.CreateProject("Aligned");
        project.Environments[0].BaseUrl = "https://example.test";
        project.RouteParts.Clear();
        var api = new RoutePart { Name = "API", Value = "/api" };
        var version = new RoutePart { Name = "Version", Value = "/v1" };
        project.RouteParts.Add(api);
        project.RouteParts.Add(version);
        var group = ProjectOperations.AddGroup(project, null, "Users").Value!;
        var endpoint = ProjectOperations.AddEndpoint(project, group.Id, "List").Value!;
        endpoint.Request!.Route = "/users";
        endpoint.Request.QueryParameters.Add(new RequestField("page", "1"));
        endpoint.Request.QueryParameters.Add(new RequestField("sort", "name"));

        RouteReorderService.MoveProjectRoutePart(project, version.Id, 0);
        RouteReorderService.MoveQueryParameter(endpoint.Request, 1, 0);
        var prepared = await RequestPreparationService.PrepareAsync(
            project, project.Environments[0], endpoint, TimeSpan.FromSeconds(5),
            new InMemorySecureValueStore(), null);
        var catalog = await RouteCatalogBuilder.BuildAsync(
            project, project.Environments[0], new InMemorySecureValueStore());
        var entry = catalog.Entries.Single();
        Equal("/v1/api/users", entry.RouteTemplate);
        Equal(prepared.Plan!.DisplayUrl, entry.ExampleUrl);
        True(entry.ExampleUrl.Contains("?sort=name&page=1"), "Query order did not reach the request plan.");
        True(
            ApiDocumentationMarkdownGenerator.Generate(catalog).Contains(entry.RouteTemplate),
            "Documentation diverged from the shared catalog.");
    }

    static async Task MigratesLegacyDocumentationDefaults()
    {
        await WithTemporaryDirectory(async directory =>
        {
            foreach (var version in new[] { 2, 3 })
            {
                var path = Path.Combine(directory, $"v{version}.probeloom.json");
                await File.WriteAllTextAsync(
                    path,
                    $$"""
                    {
                      "format": "ProbeLoom.Project",
                      "version": {{version}},
                      "project": {
                        "id": "11111111-1111-1111-1111-111111111111",
                        "name": "Legacy",
                        "environments": [],
                        "variables": [],
                        "routeParts": [],
                        "items": []
                      }
                    }
                    """);
                var project = await new ProjectFileStore().LoadAsync(path);
                Equal(string.Empty, project.Description);
                var upgraded = Path.Combine(directory, $"v{version}-upgraded.probeloom.json");
                await new ProjectFileStore().SaveAsync(upgraded, project);
                True(
                    (await File.ReadAllTextAsync(upgraded)).Contains("\"version\": 4"),
                    $"v{version} was not upgraded to v4.");
            }
        });
    }

    static async Task BuildsLargeRouteCatalog()
    {
        var project = ProjectOperations.CreateProject("Large");
        project.Environments[0].BaseUrl = "https://example.test";
        for (var groupIndex = 0; groupIndex < 10; groupIndex++)
        {
            var group = ProjectOperations.AddGroup(project, null, $"Group {groupIndex}").Value!;
            for (var endpointIndex = 0; endpointIndex < 40; endpointIndex++)
            {
                var endpoint = ProjectOperations.AddEndpoint(
                    project, group.Id, $"Endpoint {endpointIndex}").Value!;
                endpoint.Request!.Route = $"/g{groupIndex}/e{endpointIndex}";
            }
        }
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var catalog = await RouteCatalogBuilder.BuildAsync(
            project, project.Environments[0], new InMemorySecureValueStore());
        stopwatch.Stop();
        Equal(400, catalog.Entries.Count);
        True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Catalog build took {stopwatch.Elapsed}.");
    }

    static ProjectDocument CreateCatalogProjectWithSecret()
    {
        var project = ProjectOperations.CreateProject("Catalog refresh");
        project.Environments[0].BaseUrl = "https://example.test";
        var secret = new VariableDefinition
        {
            Name = "route.id",
            IsSecret = true
        };
        project.Variables.Add(secret);
        var group = ProjectOperations.AddGroup(project, null, "Items").Value!;
        var endpoint = ProjectOperations.AddEndpoint(project, group.Id, "By id").Value!;
        endpoint.Request!.Route = "/items/{{route.id}}";
        return project;
    }

}
