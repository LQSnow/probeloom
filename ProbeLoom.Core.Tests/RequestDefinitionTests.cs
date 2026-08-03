namespace ProbeLoom.Core.Tests;

internal static partial class CoreTests
{
    static void JoinsBaseUrlAndRoute()
    {
        Equal(
            "https://api.example.com/v1/users",
            RequestUrlComposer.Compose("https://api.example.com/", "/v1/users"));
    }

    static void EncodesQueryParameters()
    {
        var fields = new[]
        {
            new RequestField("search", "Ada Lovelace"),
            new RequestField("ignored", "value", false),
            new RequestField("page", "2")
        };

        Equal(
            "https://api.example.com/users?search=Ada%20Lovelace&page=2",
            RequestUrlComposer.Compose("https://api.example.com", "/users", fields));
    }

    static void ReportsUrlSources()
    {
        var result = RequestUrlComposer.ComposeDetailed(
            "https://api.example.com",
            "/users",
            [new RequestField("page", "2")]);
        Equal("https://api.example.com", result.BaseUrl);
        Equal("/users", result.RoutePath);
        Equal(1, result.QueryParameters.Count);
        Equal(3, result.Sources.Count);
    }

    static void PreservesRouteQueryString()
    {
        Equal(
            "https://api.example.com/users?sort=name&page=2",
            RequestUrlComposer.Compose(
                "https://api.example.com",
                "/users?sort=name",
                [new RequestField("page", "2")]));
    }

    static void ComposesNestedRouteParts()
    {
        var project = ProjectOperations.CreateProject("Sessions API");
        project.RouteParts[0].IsEnabled = true;
        project.RouteParts[1].IsEnabled = true;
        var auth = ProjectOperations.AddGroup(project, null, "Auth").Value!;
        auth.IsRoutePrefixEnabled = true;
        auth.RoutePrefix = "/auth";
        var nested = ProjectOperations.AddGroup(project, auth.Id, "Sessions").Value!;
        nested.IsRoutePrefixEnabled = true;
        nested.RoutePrefix = "/sessions";
        var endpoint = ProjectOperations.AddEndpoint(project, nested.Id, "Get session").Value!;
        endpoint.Request!.Route = "/{id}";
        endpoint.Request.PathParameters.Add(new RequestField("id", "42"));

        var result = RequestUrlComposer.ComposeDetailed(project, project.Environments[0], endpoint);
        Equal("http://localhost:5080/api/v1/auth/sessions/42", result.FinalUrl);
        Equal(2, result.Sources.Count(source => source.Kind == UrlSourceKind.Group));
        True(result.Sources.Any(source => source.Label == "API Version"), "Project source was not reported.");
    }

    static void IgnoresOptionalRoutePrefixes()
    {
        var project = ProjectOperations.CreateProject("API");
        project.RouteParts.Clear();
        project.RouteParts.Add(new RoutePart { Name = "Empty", Value = string.Empty, IsEnabled = false });
        var group = ProjectOperations.AddGroup(project, null, "Organize only").Value!;
        group.RoutePrefix = string.Empty;
        group.IsRoutePrefixEnabled = false;
        var endpoint = ProjectOperations.AddEndpoint(project, group.Id, "Health").Value!;
        endpoint.Request!.Route = "/health";

        Equal(
            "http://localhost:5080/health",
            RequestUrlComposer.ComposeDetailed(project, project.Environments[0], endpoint).FinalUrl);
    }

    static void EncodesPathParameters()
    {
        var project = ProjectOperations.CreateProject("API");
        project.RouteParts.Clear();
        var group = ProjectOperations.AddGroup(project, null, "Users").Value!;
        var endpoint = ProjectOperations.AddEndpoint(project, group.Id, "User").Value!;
        endpoint.Request!.Route = "/users/{id}/files";
        endpoint.Request.PathParameters.Add(new RequestField("id", "Ada Lovelace/研发"));
        endpoint.Request.QueryParameters.Add(new RequestField("filter", "active users"));

        Equal(
            "http://localhost:5080/users/Ada%20Lovelace%2F%E7%A0%94%E5%8F%91/files?filter=active%20users",
            RequestUrlComposer.ComposeDetailed(project, project.Environments[0], endpoint).FinalUrl);
    }

    static void ReportsMissingPathParameters()
    {
        var project = ProjectOperations.CreateProject("API");
        var group = ProjectOperations.AddGroup(project, null, "Users").Value!;
        var endpoint = ProjectOperations.AddEndpoint(project, group.Id, "User").Value!;
        endpoint.Request!.Route = "/users/{id}";

        var result = RequestValidator.Validate(project, project.Environments[0], endpoint);
        True(!result.IsValid, "Missing path parameters should fail validation.");
        True(
            result.Issues.Any(issue => issue.Target == ValidationTarget.PathParameters),
            "Expected a structured path parameter issue.");
    }

    static void AcceptsValidRequest()
    {
        var result = RequestValidator.Validate(
            "POST",
            "https://api.example.com",
            "/users",
            [new RequestField("notify", "true")],
            [new RequestField("Content-Type", "application/json")],
            """{"name":"Ada"}""");

        True(result.IsValid, "Expected request to be valid.");
        Equal("https://api.example.com/users?notify=true", result.FinalUrl);
    }

    static void RejectsInvalidUrlParts()
    {
        var result = RequestValidator.Validate("GET", "not-a-url", "users", [], [], string.Empty);
        True(!result.IsValid, "Invalid URL parts should be rejected.");
        True(result.Issues.Any(issue => issue.Target == ValidationTarget.BaseUrl), "Missing Base URL issue.");
        True(result.Issues.Any(issue => issue.Target == ValidationTarget.Route), "Missing Route issue.");
    }

    static void RejectsInvalidJson()
    {
        var result = RequestValidator.Validate(
            "POST",
            "https://api.example.com",
            "/users",
            [],
            [],
            """{"name":}""");

        True(!result.IsValid, "Expected malformed JSON to be rejected.");
        True(
            result.Issues.Any(issue =>
                issue.Target == ValidationTarget.RawJsonBody && issue.Message.Contains("第 1 行")),
            "Expected a located JSON validation error.");
    }

    static void FormatsJson()
    {
        var result = JsonBodyFormatter.Format("""{"name":"Ada","active":true}""");
        True(result.Succeeded, result.Error ?? "Formatting failed.");
        True(result.FormattedJson.Contains(Environment.NewLine), "Formatted JSON should contain line breaks.");
    }

    static void RejectsDuplicateHeaders()
    {
        var result = RequestValidator.Validate(
            "GET",
            "https://api.example.com",
            "/users",
            [],
            [
                new RequestField("Accept", "application/json"),
                new RequestField("accept", "text/plain")
            ],
            string.Empty);

        True(!result.IsValid, "Expected duplicate headers to be rejected.");
    }

    static void RejectsNamelessEnabledFields()
    {
        var result = RequestValidator.Validate(
            "GET",
            "https://api.example.com",
            "/users",
            [new RequestField("", "value")],
            [new RequestField("", "application/json")],
            string.Empty);
        True(
            result.Issues.Any(issue => issue.Target == ValidationTarget.QueryParameters),
            "Nameless query field should be rejected.");
        True(
            result.Issues.Any(issue => issue.Target == ValidationTarget.Headers),
            "Nameless header should be rejected.");
    }

    static void ReordersRouteBlocks()
    {
        var project = ProjectOperations.CreateProject("Routes");
        project.RouteParts.Clear();
        var api = new RoutePart { Name = "API", Value = "/api" };
        var version = new RoutePart { Name = "Version", Value = "/v1" };
        project.RouteParts.Add(api);
        project.RouteParts.Add(version);
        True(RouteReorderService.MoveProjectRoutePart(project, version.Id, 0), "Route part did not move.");
        Equal(version.Id, project.RouteParts[0].Id);

        var request = new RequestDefinition();
        request.QueryParameters.Add(new RequestField("a", "1"));
        request.QueryParameters.Add(new RequestField("b", "2"));
        True(RouteReorderService.MoveQueryParameter(request, 1, 0), "Query parameter did not move.");
        Equal("b", request.QueryParameters[0].Name);
        True(!RouteReorderService.CanMove(RouteCompositionPartKind.GroupPrefix), "Locked group block became movable.");
        True(!RouteReorderService.CanMove(RouteCompositionPartKind.EndpointRoute), "Locked endpoint block became movable.");
    }

}
