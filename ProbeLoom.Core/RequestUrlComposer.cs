using System.Text;
using System.Text.RegularExpressions;

namespace ProbeLoom.Core;

public enum UrlSourceKind
{
    Environment,
    Project,
    Group,
    Endpoint,
    PathParameter,
    QueryParameter
}

public sealed record UrlSourcePart(
    string Label,
    string Value,
    string Source,
    UrlSourceKind Kind,
    bool IsEnabled = true);

public sealed record RequestUrlBreakdown(
    string BaseUrl,
    string RoutePath,
    string RouteQuery,
    IReadOnlyList<RequestField> QueryParameters,
    string FinalUrl,
    IReadOnlyList<UrlSourcePart> Sources,
    IReadOnlyList<string> MissingPathParameters)
{
    public Uri? FinalUri =>
        Uri.TryCreate(FinalUrl, UriKind.Absolute, out var uri) ? uri : null;
}

public sealed record RouteTemplatePart(
    UrlSourceKind Kind,
    string Label,
    string Value,
    string Source,
    bool IsEnabled);

public static partial class RequestUrlComposer
{
    public static string Compose(
        string baseUrl,
        string route,
        IEnumerable<RequestField>? queryParameters = null) =>
        ComposeDetailed(baseUrl, route, queryParameters).FinalUrl;

    public static RequestUrlBreakdown ComposeDetailed(
        string baseUrl,
        string route,
        IEnumerable<RequestField>? queryParameters = null)
    {
        var temporaryProject = new ProjectDocument();
        var temporaryEnvironment = new ProjectEnvironment { Name = "当前 Environment", BaseUrl = baseUrl };
        var temporaryNode = new ProjectNode
        {
            Kind = ProjectNodeKind.Endpoint,
            Name = "当前请求",
            Request = new RequestDefinition
            {
                Route = route,
                QueryParameters = new System.Collections.ObjectModel.ObservableCollection<RequestField>(
                    (queryParameters ?? []).Select(field => field.Clone()))
            }
        };
        return ComposeDetailed(temporaryProject, temporaryEnvironment, temporaryNode);
    }

    public static RequestUrlBreakdown ComposeDetailed(
        ProjectDocument project,
        ProjectEnvironment? environment,
        ProjectNode node)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(node);

        var request = node.Request ?? throw new ArgumentException("节点不包含请求定义。", nameof(node));
        var sources = new List<UrlSourcePart>();
        var pathParts = new List<string>();
        var missingParameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parameters = request.PathParameters
            .Where(field => field.IsEnabled && !string.IsNullOrWhiteSpace(field.Name))
            .GroupBy(field => field.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.OrdinalIgnoreCase);

        var normalizedBase = (environment?.BaseUrl ?? string.Empty).Trim().TrimEnd('/');
        sources.Add(new UrlSourcePart(
            "Base URL",
            string.IsNullOrWhiteSpace(normalizedBase) ? "未配置" : normalizedBase,
            environment?.Name ?? "未选择 Environment",
            UrlSourceKind.Environment));

        foreach (var contribution in GetRouteTemplateParts(project, node))
        {
            sources.Add(new UrlSourcePart(
                contribution.Label,
                string.IsNullOrWhiteSpace(contribution.Value) ? "未配置" : contribution.Value,
                contribution.Source,
                contribution.Kind,
                contribution.IsEnabled));
            if (contribution.IsEnabled)
            {
                AddPathPart(pathParts, contribution.Value, parameters, missingParameters);
            }
        }

        var routeParts = (request.Route ?? string.Empty).Trim().Split('?', 2);
        var endpointPath = routeParts[0];
        var routeQuery = routeParts.Length == 2 ? routeParts[1].Trim() : string.Empty;

        foreach (var pathParameter in request.PathParameters)
        {
            sources.Add(new UrlSourcePart(
                $"{{{pathParameter.Name}}}",
                pathParameter.Value,
                "Path Parameters",
                UrlSourceKind.PathParameter,
                pathParameter.IsEnabled));
        }

        var enabledQuery = request.QueryParameters
            .Where(field => field.IsEnabled && !string.IsNullOrWhiteSpace(field.Name))
            .Select(field => field.Clone())
            .ToArray();
        foreach (var query in request.QueryParameters)
        {
            sources.Add(new UrlSourcePart(
                query.Name,
                query.Value,
                "Query Parameters",
                UrlSourceKind.QueryParameter,
                query.IsEnabled));
        }

        var combinedPath = pathParts.Count == 0 ? string.Empty : $"/{string.Join("/", pathParts)}";
        var combined = string.IsNullOrWhiteSpace(normalizedBase)
            ? combinedPath
            : $"{normalizedBase}{combinedPath}";

        var queryParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(routeQuery))
        {
            queryParts.Add(routeQuery);
        }

        queryParts.AddRange(enabledQuery.Select(field =>
            $"{Uri.EscapeDataString(field.Name.Trim())}={Uri.EscapeDataString(field.Value)}"));
        var finalUrl = queryParts.Count == 0
            ? combined
            : $"{combined}?{string.Join("&", queryParts)}";
        if (missingParameters.Count == 0 &&
            Uri.TryCreate(finalUrl, UriKind.Absolute, out var canonicalUri) &&
            (canonicalUri.Scheme == Uri.UriSchemeHttp || canonicalUri.Scheme == Uri.UriSchemeHttps))
        {
            finalUrl = canonicalUri.AbsoluteUri;
        }

        return new RequestUrlBreakdown(
            normalizedBase,
            endpointPath,
            routeQuery,
            enabledQuery,
            finalUrl,
            sources,
            missingParameters.ToArray());
    }

    public static IReadOnlyList<RouteTemplatePart> GetRouteTemplateParts(
        ProjectDocument project,
        ProjectNode node)
    {
        var parts = new List<RouteTemplatePart>();
        parts.AddRange(project.RouteParts.Select(part => new RouteTemplatePart(
            UrlSourceKind.Project,
            string.IsNullOrWhiteSpace(part.Name) ? "Project Route Part" : part.Name.Trim(),
            part.Value,
            project.Name,
            part.IsEnabled)));
        parts.AddRange(ProjectOperations.GetAncestors(project, node.Id)
            .Where(item => item.Kind == ProjectNodeKind.Group)
            .Select(group => new RouteTemplatePart(
                UrlSourceKind.Group,
                "Group Prefix",
                group.RoutePrefix,
                group.Name,
                group.IsRoutePrefixEnabled)));
        var route = (node.Request?.Route ?? string.Empty).Trim().Split('?', 2)[0];
        parts.Add(new RouteTemplatePart(
            UrlSourceKind.Endpoint,
            node.Kind == ProjectNodeKind.RequestCase ? "Request Case Route" : "Endpoint Route",
            route,
            node.Name,
            true));
        return parts;
    }

    public static string ComposeRouteTemplate(ProjectDocument project, ProjectNode node)
    {
        var segments = GetRouteTemplateParts(project, node)
            .Where(part => part.IsEnabled)
            .SelectMany(part => (part.Value ?? string.Empty).Trim().Trim('/').Split(
                '/', StringSplitOptions.RemoveEmptyEntries));
        return "/" + string.Join("/", segments);
    }

    private static void AddPathPart(
        ICollection<string> target,
        string? rawPart,
        IReadOnlyDictionary<string, string> parameters,
        ISet<string> missingParameters)
    {
        var value = (rawPart ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        foreach (var segment in value.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            target.Add(EncodeSegment(segment, parameters, missingParameters));
        }
    }

    private static string EncodeSegment(
        string segment,
        IReadOnlyDictionary<string, string> parameters,
        ISet<string> missingParameters)
    {
        var result = new StringBuilder();
        var cursor = 0;
        foreach (Match match in PathParameterPattern().Matches(segment))
        {
            AppendStatic(result, segment[cursor..match.Index]);
            var name = match.Groups["name"].Value;
            if (parameters.TryGetValue(name, out var value))
            {
                result.Append(Uri.EscapeDataString(value));
            }
            else
            {
                missingParameters.Add(name);
                result.Append(match.Value);
            }

            cursor = match.Index + match.Length;
        }

        AppendStatic(result, segment[cursor..]);
        return result.ToString();
    }

    private static void AppendStatic(StringBuilder builder, string value)
    {
        if (value.Length == 0)
        {
            return;
        }

        try
        {
            builder.Append(Uri.EscapeDataString(Uri.UnescapeDataString(value)));
        }
        catch (UriFormatException)
        {
            builder.Append(Uri.EscapeDataString(value));
        }
    }

    [GeneratedRegex(@"\{(?<name>[A-Za-z_][A-Za-z0-9_.-]*)\}")]
    private static partial Regex PathParameterPattern();
}
