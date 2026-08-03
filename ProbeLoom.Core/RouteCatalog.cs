using System.Text;
using System.Text.RegularExpressions;

namespace ProbeLoom.Core;

public enum RouteConflictKind
{
    ExactDuplicate,
    ParameterizedDuplicate,
    Invalid
}

public sealed record DocumentedRequestField(
    string Name,
    string Value,
    string Description,
    bool IsEnabled);

public sealed record RouteCatalogEntry(
    Guid NodeId,
    Guid? EndpointId,
    ProjectNodeKind Kind,
    string Name,
    IReadOnlyList<string> GroupPath,
    IReadOnlyList<Guid> GroupIds,
    string Method,
    string RouteTemplate,
    string NormalizedRoute,
    string ExampleUrl,
    string Authentication,
    bool HasVariables,
    bool HasSecrets,
    bool IsValid,
    IReadOnlyList<string> ValidationMessages,
    string Summary,
    string Description,
    IReadOnlyList<string> Tags,
    bool IsDeprecated,
    IReadOnlyList<DocumentedRequestField> PathParameters,
    IReadOnlyList<DocumentedRequestField> QueryParameters,
    IReadOnlyList<DocumentedRequestField> Headers,
    string RequestBody,
    string MaskedCurl)
{
    public string GroupDisplay => string.Join(" / ", GroupPath);
}

public sealed record RouteConflict(
    RouteConflictKind Kind,
    string Method,
    string NormalizedRoute,
    IReadOnlyList<Guid> NodeIds,
    string Message);

public sealed record RouteCatalog(
    Guid ProjectId,
    string ProjectName,
    string ProjectDescription,
    Guid? EnvironmentId,
    string EnvironmentName,
    IReadOnlyList<RouteCatalogEntry> Entries,
    IReadOnlyList<RouteConflict> Conflicts);

public static partial class RouteCatalogBuilder
{
    public static async Task<RouteCatalog> BuildAsync(
        ProjectDocument project,
        ProjectEnvironment? environment,
        ISecureValueStore secureValueStore,
        TokenSession? tokenSession = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        var entries = new List<RouteCatalogEntry>();
        foreach (var node in ProjectOperations.EnumerateNodes(project.Items)
                     .Where(item => item.Request is not null))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = node.Request!;
            PreparedRequestResult? prepared = null;
            string? preparationError = null;
            try
            {
                prepared = await RequestPreparationService.PrepareAsync(
                    project,
                    environment,
                    node,
                    TimeSpan.FromSeconds(30),
                    secureValueStore,
                    tokenSession,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                preparationError = exception.Message;
            }

            var template = RequestUrlComposer.ComposeRouteTemplate(project, node);
            var snapshot = prepared is null ? null : FinalRequestSnapshotFactory.Create(prepared);
            var validation = prepared?.Validation.Issues.Select(issue => issue.Message).ToList() ?? [];
            if (!string.IsNullOrWhiteSpace(preparationError))
            {
                validation.Add(preparationError);
            }

            var endpoint = node.Kind == ProjectNodeKind.RequestCase
                ? ProjectOperations.FindParent(project, node.Id)
                : node;
            var groups = ProjectOperations.GetAncestors(project, node.Id)
                .Where(item => item.Kind == ProjectNodeKind.Group)
                .ToArray();
            var curl = snapshot is null ? string.Empty : PowerShellCurlExporter.Export(snapshot).Command;
            entries.Add(new RouteCatalogEntry(
                node.Id,
                endpoint?.Id,
                node.Kind,
                node.Name,
                groups.Select(group => group.Name).ToArray(),
                groups.Select(group => group.Id).ToArray(),
                request.Method.Trim().ToUpperInvariant(),
                template,
                Normalize(template),
                snapshot?.Url ?? prepared?.DisplayUrlBreakdown.FinalUrl ?? string.Empty,
                prepared?.AuthenticationSummary ?? request.Authentication.Kind.ToString(),
                ContainsVariable(project, node),
                prepared?.VariableResolution.Variables.Values.Any(variable => variable.IsSecret) == true,
                prepared?.Validation.IsValid == true && preparationError is null,
                validation,
                node.Summary,
                node.Description,
                node.Tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                node.IsDeprecated,
                Fields(request.PathParameters, maskValues: true),
                Fields(request.QueryParameters, maskValues: true),
                Fields(request.Headers, maskValues: true),
                SensitiveDataMasker.MaskJsonBody(request.RawJsonBody),
                curl));
        }

        return new RouteCatalog(
            project.Id,
            project.Name,
            project.Description,
            environment?.Id,
            environment?.Name ?? string.Empty,
            entries,
            FindConflicts(entries));
    }

    public static string Normalize(string route)
    {
        var path = "/" + string.Join(
            "/",
            (route ?? string.Empty).Split('?', 2)[0]
                .Trim()
                .Trim('/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(segment => ParameterPattern().Replace(segment, "{}")));
        return path.Length == 0 ? "/" : path.ToLowerInvariant();
    }

    public static IReadOnlyList<RouteConflict> FindConflicts(
        IReadOnlyList<RouteCatalogEntry> entries)
    {
        var result = entries
            .Where(entry => !entry.IsValid || string.IsNullOrWhiteSpace(entry.NormalizedRoute))
            .Select(entry => new RouteConflict(
                RouteConflictKind.Invalid,
                entry.Method,
                entry.NormalizedRoute,
                [entry.NodeId],
                $"{entry.Name} has an invalid or unresolved route."))
            .ToList();

        foreach (var group in entries
                     .Where(entry => entry.IsValid)
                     .GroupBy(entry => (entry.Method, entry.NormalizedRoute)))
        {
            var values = group.ToArray();
            if (values.Length < 2)
            {
                continue;
            }

            var exact = values.Select(value => CanonicalExact(value.RouteTemplate))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() == 1;
            result.Add(new RouteConflict(
                exact ? RouteConflictKind.ExactDuplicate : RouteConflictKind.ParameterizedDuplicate,
                group.Key.Method,
                group.Key.NormalizedRoute,
                values.Select(value => value.NodeId).ToArray(),
                exact
                    ? $"Duplicate route: {group.Key.Method} {values[0].RouteTemplate}"
                    : $"Structurally equivalent parameterized routes: {group.Key.Method} {group.Key.NormalizedRoute}"));
        }
        return result;
    }

    private static string CanonicalExact(string route) =>
        "/" + string.Join("/", route.Trim('/').Split(
            '/', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    private static IReadOnlyList<DocumentedRequestField> Fields(
        IEnumerable<RequestField> fields,
        bool maskValues = false) =>
        fields.Select(field => new DocumentedRequestField(
            field.Name,
            maskValues ? SensitiveDataMasker.MaskHeader(field.Name, field.Value) : field.Value,
            field.Description,
            field.IsEnabled)).ToArray();

    private static bool ContainsVariable(ProjectDocument project, ProjectNode node)
    {
        var values = RequestUrlComposer.GetRouteTemplateParts(project, node).Select(part => part.Value)
            .Concat([
                node.Request?.Method ?? string.Empty,
                node.Request?.RawJsonBody ?? string.Empty
            ])
            .Concat(node.Request?.PathParameters.SelectMany(field => new[] { field.Name, field.Value }) ?? [])
            .Concat(node.Request?.QueryParameters.SelectMany(field => new[] { field.Name, field.Value }) ?? [])
            .Concat(node.Request?.Headers.SelectMany(field => new[] { field.Name, field.Value }) ?? []);
        return values.Any(value => value.Contains("{{", StringComparison.Ordinal));
    }

    [GeneratedRegex(@"\{[A-Za-z_][A-Za-z0-9_.-]*\}")]
    private static partial Regex ParameterPattern();
}

public enum DocumentationScope
{
    Project,
    Group,
    Endpoint
}

public static class ApiDocumentationMarkdownGenerator
{
    public static string Generate(
        RouteCatalog catalog,
        DocumentationScope scope = DocumentationScope.Project,
        Guid? scopeNodeId = null)
    {
        var entries = scope switch
        {
            DocumentationScope.Group when scopeNodeId is Guid groupId =>
                catalog.Entries.Where(entry => entry.GroupIds.Contains(groupId)).ToArray(),
            DocumentationScope.Endpoint when scopeNodeId is Guid endpointId =>
                catalog.Entries.Where(entry => entry.EndpointId == endpointId).ToArray(),
            _ => catalog.Entries.ToArray()
        };
        var text = new StringBuilder();
        text.AppendLine($"# {catalog.ProjectName}");
        if (!string.IsNullOrWhiteSpace(catalog.ProjectDescription))
        {
            text.AppendLine().AppendLine(catalog.ProjectDescription);
        }
        text.AppendLine().AppendLine("## Contents");
        foreach (var entry in entries.Where(item => item.Kind == ProjectNodeKind.Endpoint))
        {
            text.AppendLine($"- [{entry.Method} {entry.Name}](#{Anchor(entry)})");
        }

        foreach (var entry in entries)
        {
            text.AppendLine().AppendLine($"<a id=\"{Anchor(entry)}\"></a>");
            text.AppendLine($"## {entry.Method} {entry.Name}");
            if (entry.IsDeprecated) text.AppendLine().AppendLine("> Deprecated");
            if (!string.IsNullOrWhiteSpace(entry.Summary)) text.AppendLine().AppendLine(entry.Summary);
            if (!string.IsNullOrWhiteSpace(entry.Description)) text.AppendLine().AppendLine(entry.Description);
            if (entry.Tags.Count > 0) text.AppendLine().AppendLine($"Tags: {string.Join(", ", entry.Tags)}");
            text.AppendLine().AppendLine($"- Group: {entry.GroupDisplay}");
            text.AppendLine($"- Route: `{entry.RouteTemplate}`");
            if (!string.IsNullOrWhiteSpace(entry.ExampleUrl))
                text.AppendLine($"- {catalog.EnvironmentName} example: `{entry.ExampleUrl}`");
            text.AppendLine($"- Authentication: {entry.Authentication}");
            text.AppendLine($"- Validation: {(entry.IsValid ? "Valid" : string.Join("; ", entry.ValidationMessages))}");
            AppendFields(text, "Path Parameters", entry.PathParameters);
            AppendFields(text, "Query Parameters", entry.QueryParameters);
            AppendFields(text, "Headers", entry.Headers);
            if (!string.IsNullOrWhiteSpace(entry.RequestBody))
                text.AppendLine().AppendLine("### Request Body").AppendLine("```json")
                    .AppendLine(entry.RequestBody).AppendLine("```");
            if (!string.IsNullOrWhiteSpace(entry.MaskedCurl))
                text.AppendLine().AppendLine("### PowerShell curl (masked)").AppendLine("```powershell")
                    .AppendLine(entry.MaskedCurl).AppendLine("```");
        }
        if (catalog.Conflicts.Count > 0)
        {
            text.AppendLine().AppendLine("## Route conflicts");
            foreach (var conflict in catalog.Conflicts)
                text.AppendLine($"- {conflict.Message}");
        }
        return text.ToString().TrimEnd();
    }

    public static string Anchor(RouteCatalogEntry entry) =>
        $"route-{entry.NodeId:N}-{Slug(entry.Method)}-{Slug(entry.Name)}";

    private static string Slug(string value)
    {
        var characters = value.ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '-').ToArray();
        return string.Join("-", new string(characters)
            .Split('-', StringSplitOptions.RemoveEmptyEntries));
    }

    private static void AppendFields(
        StringBuilder text,
        string heading,
        IReadOnlyList<DocumentedRequestField> fields)
    {
        var enabled = fields.Where(field => field.IsEnabled).ToArray();
        if (enabled.Length == 0) return;
        text.AppendLine().AppendLine($"### {heading}");
        text.AppendLine("| Name | Value | Description |").AppendLine("|---|---|---|");
        foreach (var field in enabled)
            text.AppendLine($"| {Escape(field.Name)} | `{Escape(field.Value)}` | {Escape(field.Description)} |");
    }

    private static string Escape(string value) =>
        (value ?? string.Empty).Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
}

public static class RouteReorderService
{
    public static bool MoveProjectRoutePart(ProjectDocument project, Guid sourceId, int targetIndex)
    {
        var sourceIndex = project.RouteParts.ToList().FindIndex(part => part.Id == sourceId);
        if (sourceIndex < 0 || targetIndex < 0 || targetIndex >= project.RouteParts.Count ||
            sourceIndex == targetIndex) return false;
        project.RouteParts.Move(sourceIndex, targetIndex);
        return true;
    }

    public static bool MoveQueryParameter(RequestDefinition request, int sourceIndex, int targetIndex)
    {
        if (sourceIndex < 0 || targetIndex < 0 ||
            sourceIndex >= request.QueryParameters.Count ||
            targetIndex >= request.QueryParameters.Count ||
            sourceIndex == targetIndex) return false;
        request.QueryParameters.Move(sourceIndex, targetIndex);
        return true;
    }

    public static bool CanMove(RouteCompositionPartKind kind) =>
        kind is RouteCompositionPartKind.ProjectRoutePart or RouteCompositionPartKind.QueryParameter;
}
