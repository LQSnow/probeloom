namespace ProbeLoom.Core.Tests;

internal static partial class CoreTests
{
    static async Task BuildsRouteCompositionInspection()
    {
        var project = ProjectOperations.CreateProject("Composition");
        project.RouteParts.Clear();
        project.RouteParts.Add(new RoutePart
        {
            Name = "API Version",
            Value = "/{{api.version}}",
            IsEnabled = true
        });
        project.RouteParts.Add(new RoutePart
        {
            Name = "Disabled",
            Value = "/unused",
            IsEnabled = false
        });
        project.Variables.Add(new VariableDefinition { Name = "api.version", Value = "v1" });
        var root = ProjectOperations.AddGroup(project, null, "Auth").Value!;
        root.RoutePrefix = "/auth";
        root.IsRoutePrefixEnabled = true;
        var nested = ProjectOperations.AddGroup(project, root.Id, "Organize only").Value!;
        nested.RoutePrefix = "/internal";
        nested.IsRoutePrefixEnabled = false;
        var endpoint = ProjectOperations.AddEndpoint(project, nested.Id, "Session").Value!;
        endpoint.Request!.Route = "/sessions/{resourceId}";
        endpoint.Request.PathParameters.Add(new RequestField("resourceId", "{{resource.id}}"));
        endpoint.Request.QueryParameters.Add(new RequestField("filter", "{{query.filter}}"));
        endpoint.Variables.Add(new VariableDefinition { Name = "resource.id", Value = "user 42" });
        endpoint.Variables.Add(new VariableDefinition { Name = "query.filter", Value = "active users" });
        var prepared = await RequestPreparationService.PrepareAsync(
            project,
            project.Environments[0],
            endpoint,
            TimeSpan.FromSeconds(2),
            new InMemorySecureValueStore(),
            null);
        var snapshot = RequestInspectorSnapshotFactory.Create(
            project,
            project.Environments[0],
            endpoint,
            prepared,
            null);

        Equal(
            "http://localhost:5080/v1/auth/sessions/user%2042?filter=active%20users",
            snapshot.FinalUrl);
        Equal(
            string.Join(",",
            [
                RouteCompositionPartKind.Environment,
                RouteCompositionPartKind.ProjectRoutePart,
                RouteCompositionPartKind.ProjectRoutePart,
                RouteCompositionPartKind.GroupPrefix,
                RouteCompositionPartKind.GroupPrefix,
                RouteCompositionPartKind.EndpointRoute,
                RouteCompositionPartKind.PathParameter,
                RouteCompositionPartKind.QueryParameter
            ]),
            string.Join(",", snapshot.RouteParts.Select(part => part.Kind)));
        var version = snapshot.RouteParts.Single(part => part.Label == "API Version");
        Equal("/{{api.version}}", version.TemplateValue);
        Equal("/v1", version.ResolvedValue);
        True(version.WasTransformed, "Variable replacement should be visible.");
        Equal(
            RouteCompositionPartState.Disabled,
            snapshot.RouteParts.Single(part => part.Label == "Disabled").State);
        var path = snapshot.RouteParts.Single(part => part.Kind == RouteCompositionPartKind.PathParameter);
        Equal("{{resource.id}}", path.TemplateValue);
        Equal("user 42", path.ResolvedValue);
        Equal("user%2042", path.EncodedValue);
    }

    static async Task BuildsInspectorVariableSummary()
    {
        var project = ProjectOperations.CreateProject("Variables");
        var secret = new VariableDefinition { Name = "api.secret", IsSecret = true };
        project.Variables.Add(secret);
        project.Variables.Add(new VariableDefinition { Name = "derived", Value = "Bearer {{api.secret}}" });
        project.Variables.Add(new VariableDefinition { Name = "unused", Value = "not shown" });
        var group = ProjectOperations.AddGroup(project, null, "Group").Value!;
        var endpoint = ProjectOperations.AddEndpoint(project, group.Id, "Endpoint").Value!;
        endpoint.Request!.Route = "/{{resource}}";
        endpoint.Request.Headers.Add(new RequestField("X-Key", "{{derived}}"));
        endpoint.Variables.Add(new VariableDefinition { Name = "resource", Value = "users" });
        var store = new InMemorySecureValueStore();
        await store.SetAsync(SecureValueKeys.Variable(project.Id, secret.Id), "do-not-show");
        var prepared = await RequestPreparationService.PrepareAsync(
            project,
            project.Environments[0],
            endpoint,
            TimeSpan.FromSeconds(2),
            store,
            null);
        var snapshot = RequestInspectorSnapshotFactory.Create(
            project,
            project.Environments[0],
            endpoint,
            prepared,
            null);

        Equal(3, snapshot.Variables.Count);
        True(snapshot.Variables.All(item => item.Name != "unused"), "Unused variable should not be emphasized.");
        Equal(4, snapshot.VariableBlocks.Count);
        True(
            snapshot.VariableBlocks.Any(item => item.Name == "unused" && !item.IsReferenced),
            "The draggable palette should include effective variables that are not used by this request yet.");
        True(
            snapshot.VariableBlocks.First().IsReferenced,
            "Referenced variable blocks should be presented before unused blocks.");
        var secretItem = snapshot.Variables.Single(item => item.Name == "api.secret");
        Equal("••••••", secretItem.DisplayValue);
        True(secretItem.IsSecret && secretItem.IsConfigured, "Secret status should be visible without its value.");
        var derivedItem = snapshot.Variables.Single(item => item.Name == "derived");
        Equal("••••••", derivedItem.DisplayValue);
        True(derivedItem.IsSecret, "Values derived from a secret should also be masked.");
        True(
            snapshot.RouteParts.All(part => !part.ResolvedValue.Contains("do-not-show")),
            "Route composition leaked a secret.");
    }

    static void InsertsVariableReferences()
    {
        Equal("{{api.version}}", VariableReference.Format(" api.version "));
        True(VariableReference.ContainsReference("/api/{{api.version}}"), "Variable reference should be visually discoverable.");
        True(!VariableReference.ContainsReference("/api/users"), "Plain text must not be marked as a variable reference.");

        var inserted = VariableReference.Insert("/api/old/users", 5, 3, "api.version");
        True(inserted.Succeeded, inserted.Error);
        Equal("/api/{{api.version}}/users", inserted.Text);
        Equal("/api/{{api.version}}".Length, inserted.CaretIndex);

        var replaced = VariableReference.Insert("Bearer old", 7, 3, "token.access");
        True(replaced.Succeeded, replaced.Error);
        Equal("Bearer {{token.access}}", replaced.Text);
        Equal(replaced.Text.Length, replaced.CaretIndex);

        var clamped = VariableReference.Insert("route", 100, 100, "suffix");
        Equal("route{{suffix}}", clamped.Text);

        var invalid = VariableReference.Insert("unchanged", 3, 0, "bad name");
        True(!invalid.Succeeded, "Invalid variable names must not be inserted.");
        Equal("unchanged", invalid.Text);

        True(VariableReference.TryGetAt("/v1/{{api.version}}/users", 8, out var name), "Variable reference should be detected at its caret position.");
        Equal("api.version", name);
        True(!VariableReference.TryGetAt("/v1/users", 3, out _), "Plain text must not resolve as a variable reference.");
    }

    static void AssistsJsonEditing()
    {
        var paired = JsonEditorAssist.InsertCharacter("", 0, 0, '{');
        True(paired is not null, "Opening braces should be paired.");
        Equal("{}", paired!.Text);
        Equal(1, paired.SelectionStart);

        var completedPair = JsonEditorAssist.CompleteAlreadyInsertedCharacter("{", 1, 0, '{');
        Equal("{}", completedPair!.Text);
        Equal(1, completedPair.SelectionStart);

        var completedProperty = JsonEditorAssist.CompleteAlreadyInsertedCharacter("{\"name\"", 7, 0, '"');
        Equal("{\"name\": ", completedProperty!.Text);
        Equal(9, completedProperty.SelectionStart);

        var closedProperty = JsonEditorAssist.CompleteExistingClosingQuote("{\"name\"}", 6);
        Equal("{\"name\": }", closedProperty!.Text);
        Equal(9, closedProperty.SelectionStart);

        var wrapped = JsonEditorAssist.InsertCharacter("value", 0, 5, '"');
        Equal("\"value\"", wrapped!.Text);
        Equal(1, wrapped.SelectionStart);
        Equal(5, wrapped.SelectionLength);

        var skipped = JsonEditorAssist.InsertCharacter("{}", 1, 0, '}');
        Equal("{}", skipped!.Text);
        Equal(2, skipped.SelectionStart);

        var indented = JsonEditorAssist.InsertNewLine("{}", 1, 0);
        Equal($"{{{Environment.NewLine}  {Environment.NewLine}}}", indented.Text);
        Equal(1 + Environment.NewLine.Length + 2, indented.SelectionStart);

        var commaLine = JsonEditorAssist.InsertNewLine("{\r\n  \"name\": \"Ada\"\r\n}", 18, 0);
        Equal("{\r\n  \"name\": \"Ada\",\r\n  \r\n}", commaLine.Text);
        Equal(23, commaLine.SelectionStart);

        var tabbed = JsonEditorAssist.InsertIndentation("{}", 1, 0);
        Equal("{  }", tabbed.Text);
        Equal(3, tabbed.SelectionStart);

        Equal(3, JsonEditorAssist.MapCaretIndex("a\r\nb", 4, "a\nb"));

        const string nestedSource = "{\n  \"child\": {";
        var nestedLine = JsonEditorAssist.InsertNewLine(nestedSource, nestedSource.Length, 0);
        True(
            nestedLine.Text.Contains($"{Environment.NewLine}    "),
            "A line after an opening object should gain one indentation level.");

        var deleted = JsonEditorAssist.BackspacePair("[]", 1, 0);
        Equal(string.Empty, deleted!.Text);
        Equal(0, deleted.SelectionStart);

        var completions = JsonEditorAssist.GetCompletions("{", 1);
        Equal("property", completions[0].Id);
        var completed = JsonEditorAssist.ApplyCompletion(
            "{",
            1,
            0,
            completions[0]);
        Equal("{\"property\": null", completed.Text);
        Equal(2, completed.SelectionStart);
        Equal(8, completed.SelectionLength);
    }

    static void TracksJsonEditorHistory()
    {
        var history = new TextEditHistory(2);
        var empty = new TextEditorState(string.Empty, 0, 0);
        var objectState = new TextEditorState("{}", 1, 0);
        var valueState = new TextEditorState("{\"value\": 1}", 12, 0);
        history.Record(empty);
        history.Record(objectState);

        True(history.TryUndo(valueState, out var undo), "Undo should restore the latest prior state.");
        Equal(objectState, undo);
        True(history.TryRedo(undo, out var redo), "Redo should restore the state that was undone.");
        Equal(valueState, redo);

        history.Record(empty);
        True(!history.CanRedo, "A new edit should clear redo history.");
    }

    static async Task BuildsInspectorAuthenticationSummary()
    {
        var project = ProjectOperations.CreateProject("Auth");
        var group = ProjectOperations.AddGroup(project, null, "Auth").Value!;
        var endpoint = ProjectOperations.AddEndpoint(project, group.Id, "Protected").Value!;
        endpoint.Request!.Route = "/protected";
        endpoint.Request.Authentication.Kind = AuthenticationKind.BearerToken;
        var refresh = ProjectOperations.AddEndpoint(project, group.Id, "Refresh").Value!;
        refresh.Request!.Route = "/refresh";
        project.RefreshRequestNodeId = refresh.Id;
        var session = new TokenSession(
            "secret-access-token",
            "secret-refresh-token",
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            DateTimeOffset.Parse("2025-12-31T23:00:00Z"));
        var prepared = await RequestPreparationService.PrepareAsync(
            project,
            project.Environments[0],
            endpoint,
            TimeSpan.FromSeconds(2),
            new InMemorySecureValueStore(),
            session,
            now: DateTimeOffset.Parse("2026-01-02T00:00:00Z"));
        var snapshot = RequestInspectorSnapshotFactory.Create(
            project,
            project.Environments[0],
            endpoint,
            prepared,
            session,
            DateTimeOffset.Parse("2026-01-02T00:00:00Z"));

        Equal("Bearer · Environment Token Session", snapshot.Authentication.Method);
        True(snapshot.Authentication.TokenExists, "Token existence was not reported.");
        True(snapshot.Authentication.TokenExpired, "Expired token was not reported.");
        True(snapshot.Authentication.RefreshConfigured, "Refresh configuration was not reported.");
        True(
            !snapshot.Authentication.TokenStatus.Contains("secret-access-token"),
            "Authentication summary leaked a token.");
    }

    static void ManagesInspectorLayoutState()
    {
        var normalized = new InspectorLayoutState(true, 900).Normalize();
        Equal(InspectorLayoutState.MaximumWidth, normalized.Width);
        var narrow = normalized.Decide(1100);
        True(!narrow.IsExpanded && !narrow.CanExpand, "Narrow windows should collapse the Inspector.");
        Equal(InspectorLayoutState.CollapsedWidth, narrow.Width);
        var wide = normalized.Decide(1200);
        True(wide.IsExpanded && wide.CanExpand, "Wide windows should restore the requested expanded state.");
        Equal(InspectorLayoutState.MaximumWidth, wide.Width);
        Equal(
            InspectorLayoutState.MinimumWidth,
            new InspectorLayoutState(true, 320).Resize(100, 1200).Width);
        Equal(
            375d,
            new InspectorLayoutState(true, 360).Resize(-500, 1000).Width);
    }

}
