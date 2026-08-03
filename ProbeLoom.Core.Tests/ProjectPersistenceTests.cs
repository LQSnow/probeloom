namespace ProbeLoom.Core.Tests;

internal static partial class CoreTests
{
    static async Task CoordinatesProjectLifecycle()
    {
        await WithTemporaryDirectory(async directory =>
        {
            var sessionPath = Path.Combine(directory, "session.json");
            var projectPath = Path.Combine(directory, "first.probeloom.json");
            var otherPath = Path.Combine(directory, "second.probeloom.json");
            var fileStore = new ProjectFileStore();
            var service = new ProjectLifecycleService(fileStore, sessionPath);

            var created = await service.CreateProjectAsync("First");
            True(created.Transition.PreviousProject is null, "New lifecycle should not have a previous project.");
            var firstProject = service.Project!;
            var save = await service.SaveProjectAsync(projectPath);
            Equal(Path.GetFullPath(projectPath), save.FilePath);
            True(save.Warning is null, save.Warning ?? "Unexpected recent project warning.");
            True(File.Exists(projectPath), "Project file was not saved.");
            True(File.Exists(sessionPath), "Recent project state was not saved.");

            var restoredService = new ProjectLifecycleService(fileStore, sessionPath);
            var restored = await restoredService.RestoreLastProjectAsync();
            True(restored.Error is null, restored.Error ?? "Restore failed.");
            Equal(firstProject.Id, restoredService.Project!.Id);
            Equal(Path.GetFullPath(projectPath), restoredService.ProjectFilePath);

            var secondProject = ProjectOperations.CreateProject("Second");
            await fileStore.SaveAsync(otherPath, secondProject);
            var opened = await restoredService.OpenProjectAsync(otherPath);
            Equal(firstProject.Id, opened.Transition.PreviousProject!.Id);
            Equal(secondProject.Id, opened.Transition.CurrentProject!.Id);
            Equal(Path.GetFullPath(otherPath), restoredService.ProjectFilePath);

            var deleted = await restoredService.DeleteCurrentProjectAsync();
            Equal(secondProject.Id, deleted.Transition.PreviousProject!.Id);
            True(deleted.Transition.CurrentProject is null, "Deleted project should clear the current project.");
            True(restoredService.Project is null, "Lifecycle retained a deleted project.");
            True(!File.Exists(otherPath), "Deleting a project did not remove its file.");
            True(!File.Exists(sessionPath), "Deleting a project did not clear recent project state.");
        });
    }

    static async Task PreservesCompletedProjectSave()
    {
        await WithTemporaryDirectory(async directory =>
        {
            var invalidSessionPath = Path.Combine(directory, "session-target");
            Directory.CreateDirectory(invalidSessionPath);
            var projectPath = Path.Combine(directory, "saved.probeloom.json");
            var service = new ProjectLifecycleService(new ProjectFileStore(), invalidSessionPath);

            await service.CreateProjectAsync("Saved despite warning");
            var result = await service.SaveProjectAsync(projectPath);

            True(File.Exists(projectPath), "Project file should remain saved when recent state persistence fails.");
            True(!service.Project!.IsDirty, "A successfully saved project should be clean.");
            Equal(Path.GetFullPath(projectPath), service.ProjectFilePath);
            True(result.Warning is not null, "Recent state persistence failure should be reported as a warning.");
        });
    }

    static async Task SavesAndReloadsProject()
    {
        await WithTemporaryDirectory(async directory =>
        {
            var path = Path.Combine(directory, "project.probeloom.json");
            var project = CreateCompleteProject();
            var store = new ProjectFileStore();
            await store.SaveAsync(path, project);

            True(!project.IsDirty, "Saving should clear dirty state.");
            var loaded = await store.LoadAsync(path);
            Equal(project.Id, loaded.Id);
            Equal("Staging", loaded.Environments.Single(item => item.Name == "Staging").Name);
            var endpoint = ProjectOperations.EnumerateNodes(loaded.Items)
                .Single(item => item.Kind == ProjectNodeKind.Endpoint);
            Equal("/users/{id}", endpoint.Request!.Route);
            Equal("42", endpoint.Request.PathParameters.Single().Value);
            Equal("/api", loaded.RouteParts.Single().Value);
            var nested = ProjectOperations.FindParent(loaded, endpoint.Id)!;
            Equal("/v1", nested.RoutePrefix);
            Equal(
                "https://staging.example.test/api/v1/users/42?notify=true",
                RequestUrlComposer.ComposeDetailed(
                    loaded,
                    loaded.Environments.Single(item => item.Name == "Staging"),
                    endpoint).FinalUrl);
            Equal(1, endpoint.Children.Count);
            True(!loaded.IsDirty, "Loaded project should start clean.");
        });
    }

    static async Task LoadsVersionOneProject()
    {
        await WithTemporaryDirectory(async directory =>
        {
            var path = Path.Combine(directory, "version-one.probeloom.json");
            await File.WriteAllTextAsync(
                path,
                """
                {
                  "format": "ProbeLoom.Project",
                  "version": 1,
                  "project": {
                    "id": "11111111-1111-1111-1111-111111111111",
                    "name": "Legacy",
                    "environments": [
                      {
                        "id": "22222222-2222-2222-2222-222222222222",
                        "name": "Local",
                        "baseUrl": "https://legacy.example.test"
                      }
                    ],
                    "items": [
                      {
                        "id": "33333333-3333-3333-3333-333333333333",
                        "kind": "Group",
                        "name": "Users",
                        "children": [
                          {
                            "id": "44444444-4444-4444-4444-444444444444",
                            "kind": "Endpoint",
                            "name": "List",
                            "request": {
                              "method": "GET",
                              "route": "/v1/users",
                              "rawJsonBody": "",
                              "queryParameters": [],
                              "headers": []
                            },
                            "children": []
                          }
                        ]
                      }
                    ]
                  }
                }
                """);

            var project = await new ProjectFileStore().LoadAsync(path);
            Equal(0, project.RouteParts.Count);
            var endpoint = ProjectOperations.EnumerateNodes(project.Items)
                .Single(node => node.Kind == ProjectNodeKind.Endpoint);
            Equal(0, endpoint.Request!.PathParameters.Count);
            Equal(
                "https://legacy.example.test/v1/users",
                RequestUrlComposer.ComposeDetailed(project, project.Environments[0], endpoint).FinalUrl);
            var upgradedPath = Path.Combine(directory, "upgraded.probeloom.json");
            await new ProjectFileStore().SaveAsync(upgradedPath, project);
            True(
                (await File.ReadAllTextAsync(upgradedPath)).Contains("\"version\": 4"),
                "Migrated project was not saved as version 4.");
        });
    }

    static async Task RejectsCorruptedProject()
    {
        await WithTemporaryDirectory(async directory =>
        {
            var path = Path.Combine(directory, "corrupt.json");
            await File.WriteAllTextAsync(path, "{ not valid json");
            await ThrowsAsync<ProjectFileException>(() => new ProjectFileStore().LoadAsync(path));
        });
    }

    static async Task RejectsUnsupportedVersion()
    {
        await WithTemporaryDirectory(async directory =>
        {
            var path = Path.Combine(directory, "future.json");
            await File.WriteAllTextAsync(
                path,
                """{"format":"ProbeLoom.Project","version":999,"project":{"id":"11111111-1111-1111-1111-111111111111","name":"Future","environments":[],"items":[]}}""");
            await ThrowsAsync<ProjectFileException>(() => new ProjectFileStore().LoadAsync(path));
        });
    }

    static async Task RejectsInvalidHierarchy()
    {
        await WithTemporaryDirectory(async directory =>
        {
            var path = Path.Combine(directory, "invalid-hierarchy.json");
            await File.WriteAllTextAsync(
                path,
                """
                {
                  "format": "ProbeLoom.Project",
                  "version": 1,
                  "project": {
                    "id": "11111111-1111-1111-1111-111111111111",
                    "name": "Broken",
                    "environments": [],
                    "items": [
                      {
                        "id": "22222222-2222-2222-2222-222222222222",
                        "kind": "Endpoint",
                        "name": "Root endpoint",
                        "request": {
                          "method": "GET",
                          "route": "/",
                          "rawJsonBody": "",
                          "queryParameters": [],
                          "headers": []
                        },
                        "children": []
                      }
                    ]
                  }
                }
                """);
            await ThrowsAsync<ProjectFileException>(() => new ProjectFileStore().LoadAsync(path));
        });
    }

    static async Task RepairsStaleSelections()
    {
        await WithTemporaryDirectory(async directory =>
        {
            var path = Path.Combine(directory, "stale.json");
            var project = ProjectOperations.CreateProject("API");
            project.SelectedEnvironmentId = Guid.NewGuid();
            project.SelectedNodeId = Guid.NewGuid();
            await new ProjectFileStore().SaveAsync(path, project);

            var loaded = await new ProjectFileStore().LoadAsync(path);
            Equal(loaded.Environments[0].Id, loaded.SelectedEnvironmentId);
            True(loaded.SelectedNodeId is null, "Stale node selection should be cleared.");
        });
    }

    static ProjectDocument CreateCompleteProject()
    {
        var project = ProjectOperations.CreateProject("Users API");
        project.RouteParts.Clear();
        project.RouteParts.Add(new RoutePart { Name = "API Prefix", Value = "/api", IsEnabled = true });
        ProjectOperations.AddEnvironment(project, "Staging", "https://staging.example.test");
        var group = ProjectOperations.AddGroup(project, null, "Development").Value!;
        var nested = ProjectOperations.AddGroup(project, group.Id, "Users").Value!;
        nested.RoutePrefix = "/v1";
        nested.IsRoutePrefixEnabled = true;
        var endpoint = ProjectOperations.AddEndpoint(project, nested.Id, "Create user").Value!;
        endpoint.Request!.Method = "POST";
        endpoint.Request.Route = "/users/{id}";
        endpoint.Request.PathParameters.Add(new RequestField("id", "42"));
        endpoint.Request.QueryParameters.Add(new RequestField("notify", "true"));
        endpoint.Request.RawJsonBody = """{"name":"Ada"}""";
        ProjectOperations.AddRequestCase(project, endpoint.Id, "Invalid email");
        return project;
    }

}
