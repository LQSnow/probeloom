namespace ProbeLoom.Core.Tests;

internal static partial class CoreTests
{
    static void CreatesAndRenamesProject()
    {
        var project = ProjectOperations.CreateProject("Orders API");
        Equal("Orders API", project.Name);
        Equal(1, project.Environments.Count);
        True(project.IsDirty, "A new project should be unsaved.");
        project.MarkSaved();

        var result = ProjectOperations.RenameProject(project, "Orders Service");
        True(result.Succeeded, result.Error ?? "Rename failed.");
        True(project.IsDirty, "Renaming should mark the project dirty.");
    }

    static void ManagesEnvironments()
    {
        var project = ProjectOperations.CreateProject("API");
        var created = ProjectOperations.AddEnvironment(project, "Staging", "https://staging.example.test");
        True(created.Succeeded, created.Error ?? "Environment creation failed.");

        var updated = ProjectOperations.UpdateEnvironment(
            project,
            created.Value!.Id,
            "QA",
            "https://qa.example.test");
        True(updated.Succeeded, updated.Error ?? "Environment update failed.");
        Equal("QA", created.Value.Name);

        var deleted = ProjectOperations.DeleteEnvironment(project, created.Value.Id);
        True(deleted.Succeeded, deleted.Error ?? "Environment deletion failed.");
        True(project.Environments.All(item => item.Id != created.Value.Id), "Deleted environment is still referenced.");
    }

    static void RejectsDuplicateEnvironmentNames()
    {
        var project = ProjectOperations.CreateProject("API");
        var duplicate = ProjectOperations.AddEnvironment(project, "本地", "https://other.example.test");
        True(!duplicate.Succeeded, "Duplicate environment names should be rejected.");
    }

    static void CreatesNestedWorkspace()
    {
        var project = ProjectOperations.CreateProject("API");
        var root = ProjectOperations.AddGroup(project, null, "Development").Value!;
        var nested = ProjectOperations.AddGroup(project, root.Id, "Users").Value!;
        var endpoint = ProjectOperations.AddEndpoint(project, nested.Id, "Create user").Value!;
        endpoint.Request!.Route = "/v1/users";
        var requestCase = ProjectOperations.AddRequestCase(project, endpoint.Id, "Invalid email").Value!;

        Equal(ProjectNodeKind.Group, nested.Kind);
        Equal(ProjectNodeKind.Endpoint, endpoint.Kind);
        Equal(ProjectNodeKind.RequestCase, requestCase.Kind);
        Equal("/v1/users", requestCase.Request!.Route);
        True(ReferenceEquals(ProjectOperations.FindParent(project, requestCase.Id), endpoint), "Case parent was not preserved.");
    }

    static void RejectsDuplicateSiblingNames()
    {
        var project = ProjectOperations.CreateProject("API");
        var group = ProjectOperations.AddGroup(project, null, "Users").Value!;
        True(ProjectOperations.AddEndpoint(project, group.Id, "List users").Succeeded, "First endpoint should succeed.");
        var duplicate = ProjectOperations.AddEndpoint(project, group.Id, "list users");
        True(!duplicate.Succeeded, "Sibling names should be unique ignoring case.");
    }

    static void DeletingBranchRepairsSelection()
    {
        var project = ProjectOperations.CreateProject("API");
        var first = ProjectOperations.AddGroup(project, null, "First").Value!;
        var endpoint = ProjectOperations.AddEndpoint(project, first.Id, "Health").Value!;
        var requestCase = ProjectOperations.AddRequestCase(project, endpoint.Id, "Default").Value!;
        var second = ProjectOperations.AddGroup(project, null, "Second").Value!;
        project.SelectedNodeId = requestCase.Id;

        var result = ProjectOperations.DeleteNode(project, first.Id);
        True(result.Succeeded, result.Error ?? "Delete failed.");
        Equal(second.Id, project.SelectedNodeId);
        True(ProjectOperations.FindNode(project, requestCase.Id) is null, "Deleted descendants remain reachable.");
    }

    static void TracksUnsavedChanges()
    {
        var project = ProjectOperations.CreateProject("API");
        var group = ProjectOperations.AddGroup(project, null, "Users").Value!;
        var endpoint = ProjectOperations.AddEndpoint(project, group.Id, "Create user").Value!;
        project.MarkSaved();

        endpoint.Request!.Route = "/v1/users";
        True(project.IsDirty, "Request edit did not mark project dirty.");
        project.MarkSaved();
        endpoint.Request.QueryParameters.Add(new RequestField("page", "1"));
        True(project.IsDirty, "Field collection edit did not mark project dirty.");
        project.MarkSaved();
        endpoint.Request.QueryParameters[0].Value = "2";
        True(project.IsDirty, "Field value edit did not mark project dirty.");
    }

    static void TracksRouteChanges()
    {
        var project = ProjectOperations.CreateProject("API");
        var group = ProjectOperations.AddGroup(project, null, "Users").Value!;
        var endpoint = ProjectOperations.AddEndpoint(project, group.Id, "Get user").Value!;
        project.MarkSaved();

        project.RouteParts[0].IsEnabled = true;
        True(project.IsDirty, "Project route part change did not mark the project dirty.");
        project.MarkSaved();
        group.RoutePrefix = "/users";
        True(project.IsDirty, "Group route prefix change did not mark the project dirty.");
        project.MarkSaved();
        endpoint.Request!.PathParameters.Add(new RequestField("id", "42"));
        True(project.IsDirty, "Path parameter change did not mark the project dirty.");
    }

}
