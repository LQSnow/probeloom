using System.Text;
using System.Text.RegularExpressions;

namespace ProbeLoom.Core;

public enum RouteCompositionPartKind
{
    Environment,
    ProjectRoutePart,
    GroupPrefix,
    EndpointRoute,
    PathParameter,
    QueryParameter
}

public enum RouteCompositionPartState
{
    Active,
    Disabled,
    Missing,
    Invalid
}

public enum InspectorEditTarget
{
    Environment,
    ProjectRouteParts,
    GroupPrefix,
    EndpointRoute,
    PathParameters,
    QueryParameters
}

public sealed record RouteCompositionPart(
    string Key,
    RouteCompositionPartKind Kind,
    string Label,
    string TemplateValue,
    string ResolvedValue,
    string EncodedValue,
    string SourceType,
    string SourceName,
    bool IsEnabled,
    bool WasTransformed,
    RouteCompositionPartState State,
    string StatusMessage,
    InspectorEditTarget EditTarget,
    Guid? SourceId = null,
    int? FieldIndex = null);

public sealed record InspectorVariableItem(
    string Name,
    string DisplayValue,
    string Source,
    bool IsSecret,
    bool IsConfigured,
    bool IsOverridden,
    string OverrideSummary,
    bool HasError,
    string ErrorMessage);

public sealed record InspectorVariableBlock(
    string Name,
    string Source,
    bool IsSecret,
    bool IsConfigured,
    bool IsReferenced,
    bool IsOverridden,
    bool HasError,
    string Detail);

public sealed record InspectorAuthenticationSummary(
    string Method,
    string Source,
    bool TokenExists,
    bool TokenExpired,
    bool RefreshConfigured,
    string TokenStatus);

public sealed record RequestInspectorSnapshot(
    string FinalUrl,
    IReadOnlyList<RouteCompositionPart> RouteParts,
    IReadOnlyList<InspectorVariableItem> Variables,
    IReadOnlyList<InspectorVariableBlock> VariableBlocks,
    InspectorAuthenticationSummary Authentication,
    string RequestMethod,
    int HeaderCount,
    int BodyCharacterCount,
    bool IsValid,
    IReadOnlyList<string> ValidationMessages,
    FinalRequestSnapshot? FinalRequest,
    string PowerShellCurl,
    string CurlExportError);

public static partial class RequestInspectorSnapshotFactory
{
    public static RequestInspectorSnapshot Create(
        ProjectDocument project,
        ProjectEnvironment? environment,
        ProjectNode node,
        PreparedRequestResult prepared,
        TokenSession? tokenSession,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(prepared);
        var request = node.Request ?? throw new ArgumentException(
            "节点不包含请求定义。",
            nameof(node));
        var sourceCursor = new SourceCursor(prepared.DisplayUrlBreakdown.Sources);
        var parts = new List<RouteCompositionPart>();

        var environmentSource = sourceCursor.Next(UrlSourceKind.Environment);
        parts.Add(CreatePart(
            "environment",
            RouteCompositionPartKind.Environment,
            "Base URL",
            environment?.BaseUrl ?? string.Empty,
            environmentSource?.Value ?? string.Empty,
            environmentSource?.Value ?? string.Empty,
            "Environment",
            environment?.Name ?? "未选择 Environment",
            environment is not null,
            prepared,
            InspectorEditTarget.Environment,
            environment?.Id));

        foreach (var routePart in project.RouteParts)
        {
            var source = sourceCursor.Next(UrlSourceKind.Project);
            parts.Add(CreatePart(
                $"project:{routePart.Id}",
                RouteCompositionPartKind.ProjectRoutePart,
                routePart.Name,
                routePart.Value,
                source?.Value ?? string.Empty,
                source?.Value ?? string.Empty,
                "Project",
                string.IsNullOrWhiteSpace(routePart.Name) ? project.Name : routePart.Name,
                routePart.IsEnabled,
                prepared,
                InspectorEditTarget.ProjectRouteParts,
                routePart.Id));
        }

        foreach (var group in ProjectOperations.GetAncestors(project, node.Id)
                     .Where(item => item.Kind == ProjectNodeKind.Group))
        {
            var source = sourceCursor.Next(UrlSourceKind.Group);
            parts.Add(CreatePart(
                $"group:{group.Id}",
                RouteCompositionPartKind.GroupPrefix,
                "Route Prefix",
                group.RoutePrefix,
                source?.Value ?? string.Empty,
                source?.Value ?? string.Empty,
                "Group",
                group.Name,
                group.IsRoutePrefixEnabled,
                prepared,
                InspectorEditTarget.GroupPrefix,
                group.Id));
        }

        var endpointSource = sourceCursor.Next(UrlSourceKind.Endpoint);
        parts.Add(CreatePart(
            $"endpoint:{node.Id}",
            RouteCompositionPartKind.EndpointRoute,
            node.Kind == ProjectNodeKind.RequestCase ? "Request Case Route" : "Endpoint Route",
            request.Route,
            endpointSource?.Value ?? string.Empty,
            endpointSource?.Value ?? string.Empty,
            node.Kind == ProjectNodeKind.RequestCase ? "Request Case" : "Endpoint",
            node.Name,
            true,
            prepared,
            InspectorEditTarget.EndpointRoute,
            node.Id));

        for (var index = 0; index < request.PathParameters.Count; index++)
        {
            var field = request.PathParameters[index];
            var source = sourceCursor.Next(UrlSourceKind.PathParameter);
            var resolved = source?.Value ?? string.Empty;
            parts.Add(CreatePart(
                $"path:{index}",
                RouteCompositionPartKind.PathParameter,
                string.IsNullOrWhiteSpace(field.Name) ? "未命名 Path" : field.Name,
                field.Value,
                resolved,
                Encode(resolved),
                "Path Parameter",
                string.IsNullOrWhiteSpace(field.Name) ? $"#{index + 1}" : field.Name,
                field.IsEnabled,
                prepared,
                InspectorEditTarget.PathParameters,
                node.Id,
                index));
        }

        for (var index = 0; index < request.QueryParameters.Count; index++)
        {
            var field = request.QueryParameters[index];
            var source = sourceCursor.Next(UrlSourceKind.QueryParameter);
            var resolvedName = source?.Label ?? field.Name;
            var resolvedValue = source?.Value ?? string.Empty;
            parts.Add(CreatePart(
                $"query:{index}",
                RouteCompositionPartKind.QueryParameter,
                string.IsNullOrWhiteSpace(resolvedName) ? "未命名 Query" : resolvedName,
                $"{field.Name}={field.Value}",
                $"{resolvedName}={resolvedValue}",
                $"{Encode(resolvedName)}={Encode(resolvedValue)}",
                "Query Parameter",
                string.IsNullOrWhiteSpace(field.Name) ? $"#{index + 1}" : field.Name,
                field.IsEnabled,
                prepared,
                InspectorEditTarget.QueryParameters,
                node.Id,
                index));
        }

        var variables = BuildVariableItems(project, environment, node, prepared);
        var variableBlocks = BuildVariableBlocks(prepared, variables);
        var authentication = BuildAuthentication(project, request, prepared, tokenSession, now);
        var finalRequest = FinalRequestSnapshotFactory.Create(prepared);
        var curl = finalRequest is null
            ? new CurlExportResult(false, string.Empty, "请求尚未通过校验。")
            : PowerShellCurlExporter.Export(finalRequest);
        return new RequestInspectorSnapshot(
            prepared.Plan?.DisplayUrl ?? prepared.DisplayUrlBreakdown.FinalUrl,
            parts,
            variables,
            variableBlocks,
            authentication,
            request.Method.Trim().ToUpperInvariant(),
            prepared.Plan?.Headers.Count ?? request.Headers.Count(field => field.IsEnabled),
            prepared.Plan?.Body.Length ?? request.RawJsonBody.Length,
            prepared.Validation.IsValid,
            prepared.Validation.Issues.Select(issue => issue.Message).ToArray(),
            finalRequest,
            curl.Command,
            curl.Error ?? string.Empty);
    }

    private static IReadOnlyList<InspectorVariableBlock> BuildVariableBlocks(
        PreparedRequestResult prepared,
        IReadOnlyList<InspectorVariableItem> referencedItems)
    {
        var referenced = referencedItems.Select(item => item.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var blocks = prepared.VariableResolution.Variables.Values.Select(variable =>
        {
            var issues = prepared.VariableResolution.Issues
                .Where(issue => string.Equals(
                    issue.VariableName,
                    variable.Name,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var missingSecret = variable.IsSecret &&
                                issues.Any(issue => issue.Kind == VariableIssueKind.MissingSecret);
            return new InspectorVariableBlock(
                variable.Name,
                $"{variable.Source.Scope} · {variable.Source.ScopeName}",
                variable.IsSecret,
                !missingSecret,
                referenced.Contains(variable.Name),
                variable.OverriddenSources.Count > 0,
                issues.Length > 0,
                issues.Length > 0
                    ? string.Join(" ", issues.Select(issue => issue.Message))
                    : variable.OverriddenSources.Count > 0
                        ? "覆盖较低层级定义"
                        : string.Empty);
        }).ToList();

        foreach (var missing in referencedItems.Where(item =>
                     item.HasError &&
                     blocks.All(block => !string.Equals(
                         block.Name,
                         item.Name,
                         StringComparison.OrdinalIgnoreCase))))
        {
            blocks.Add(new InspectorVariableBlock(
                missing.Name,
                missing.Source,
                missing.IsSecret,
                missing.IsConfigured,
                true,
                missing.IsOverridden,
                true,
                missing.ErrorMessage));
        }

        return blocks
            .OrderByDescending(block => block.IsReferenced)
            .ThenBy(block => block.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static RouteCompositionPart CreatePart(
        string key,
        RouteCompositionPartKind kind,
        string label,
        string template,
        string resolved,
        string encoded,
        string sourceType,
        string sourceName,
        bool enabled,
        PreparedRequestResult prepared,
        InspectorEditTarget editTarget,
        Guid? sourceId,
        int? fieldIndex = null)
    {
        template ??= string.Empty;
        resolved = NormalizeMissing(resolved);
        var hasRelevantError = HasRelevantError(kind, template, prepared);
        var state = !enabled
            ? RouteCompositionPartState.Disabled
            : hasRelevantError
                ? RouteCompositionPartState.Invalid
                : string.IsNullOrWhiteSpace(template) ||
                  string.IsNullOrWhiteSpace(resolved) ||
                  resolved == "未配置"
                    ? RouteCompositionPartState.Missing
                    : RouteCompositionPartState.Active;
        var message = state switch
        {
            RouteCompositionPartState.Disabled => "未启用，不参与最终地址",
            RouteCompositionPartState.Missing => "尚未配置",
            RouteCompositionPartState.Invalid => RelevantError(kind, template, prepared),
            _ => string.Empty
        };
        return new RouteCompositionPart(
            key,
            kind,
            label,
            template,
            resolved,
            encoded,
            sourceType,
            sourceName,
            enabled,
            !string.Equals(template, resolved, StringComparison.Ordinal) ||
            !string.Equals(resolved, encoded, StringComparison.Ordinal),
            state,
            message,
            editTarget,
            sourceId,
            fieldIndex);
    }

    private static IReadOnlyList<InspectorVariableItem> BuildVariableItems(
        ProjectDocument project,
        ProjectEnvironment? environment,
        ProjectNode node,
        PreparedRequestResult prepared)
    {
        var referenced = CollectReferencedVariables(project, environment, node, prepared.VariableResolution);
        var items = new List<InspectorVariableItem>();
        foreach (var name in referenced.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            var issues = prepared.VariableResolution.Issues
                .Where(issue => string.Equals(issue.VariableName, name, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (!prepared.VariableResolution.Variables.TryGetValue(name, out var variable))
            {
                items.Add(new InspectorVariableItem(
                    name,
                    "未解析",
                    "未定义",
                    false,
                    false,
                    false,
                    string.Empty,
                    true,
                    issues.FirstOrDefault()?.Message ?? $"缺少变量“{name}”。"));
                continue;
            }

            var missingSecret = issues.Any(issue => issue.Kind == VariableIssueKind.MissingSecret);
            var isSensitive = variable.IsSecret ||
                              prepared.VariableResolution.Variables.Values.Any(candidate =>
                                  candidate.IsSecret &&
                                  !string.IsNullOrEmpty(candidate.Value) &&
                                  variable.Value.Contains(candidate.Value, StringComparison.Ordinal));
            items.Add(new InspectorVariableItem(
                variable.Name,
                isSensitive ? "••••••" : variable.Value,
                $"{variable.Source.Scope} · {variable.Source.ScopeName}",
                isSensitive,
                !missingSecret,
                variable.OverriddenSources.Count > 0,
                variable.OverriddenSources.Count == 0
                    ? string.Empty
                    : string.Join(
                        " → ",
                        variable.OverriddenSources.Select(source =>
                            $"{source.Scope} · {source.ScopeName}")),
                issues.Length > 0,
                string.Join(" ", issues.Select(issue => issue.Message))));
        }

        foreach (var issue in prepared.VariableResolution.Issues
                     .Where(issue => !string.IsNullOrWhiteSpace(issue.VariableName) &&
                                     items.All(item => !string.Equals(
                                         item.Name,
                                         issue.VariableName,
                                         StringComparison.OrdinalIgnoreCase))))
        {
            items.Add(new InspectorVariableItem(
                issue.VariableName,
                "未解析",
                "变量解析",
                false,
                false,
                false,
                string.Empty,
                true,
                issue.Message));
        }
        return items;
    }

    private static IReadOnlySet<string> CollectReferencedVariables(
        ProjectDocument project,
        ProjectEnvironment? environment,
        ProjectNode node,
        VariableResolutionResult resolution)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var templates = new List<string>
        {
            environment?.BaseUrl ?? string.Empty,
            node.Request?.Method ?? string.Empty,
            node.Request?.Route ?? string.Empty,
            node.Request?.RawJsonBody ?? string.Empty
        };
        templates.AddRange(project.RouteParts.Select(part => part.Value));
        templates.AddRange(ProjectOperations.GetAncestors(project, node.Id)
            .Where(item => item.Kind == ProjectNodeKind.Group)
            .Select(item => item.RoutePrefix));
        if (node.Request is { } request)
        {
            templates.AddRange(request.PathParameters.SelectMany(field => new[] { field.Name, field.Value }));
            templates.AddRange(request.QueryParameters.SelectMany(field => new[] { field.Name, field.Value }));
            templates.AddRange(request.Headers.SelectMany(field => new[] { field.Name, field.Value }));
            templates.AddRange(
            [
                request.Authentication.BearerToken,
                request.Authentication.Username,
                request.Authentication.Password,
                request.Authentication.ApiKeyName,
                request.Authentication.ApiKeyValue
            ]);
        }

        foreach (var template in templates)
        {
            AddReferences(template, names);
        }

        var definitions = EnumerateVariables(project)
            .ToDictionary(variable => variable.Id);
        var queue = new Queue<string>(names);
        while (queue.TryDequeue(out var name))
        {
            if (!resolution.Variables.TryGetValue(name, out var resolved) ||
                resolved.Source.DefinitionId is not Guid definitionId ||
                !definitions.TryGetValue(definitionId, out var definition))
            {
                continue;
            }

            foreach (Match match in PlaceholderPattern().Matches(definition.Value))
            {
                if (names.Add(match.Groups["name"].Value))
                {
                    queue.Enqueue(match.Groups["name"].Value);
                }
            }
        }
        return names;
    }

    private static IEnumerable<VariableDefinition> EnumerateVariables(ProjectDocument project)
    {
        foreach (var variable in project.Variables)
        {
            yield return variable;
        }
        foreach (var environment in project.Environments)
        {
            foreach (var variable in environment.Variables)
            {
                yield return variable;
            }
        }
        foreach (var node in ProjectOperations.EnumerateNodes(project.Items))
        {
            foreach (var variable in node.Variables)
            {
                yield return variable;
            }
        }
    }

    private static InspectorAuthenticationSummary BuildAuthentication(
        ProjectDocument project,
        RequestDefinition request,
        PreparedRequestResult prepared,
        TokenSession? tokenSession,
        DateTimeOffset? now)
    {
        var method = request.Authentication.Kind switch
        {
            AuthenticationKind.None => "No Auth",
            AuthenticationKind.BearerToken when string.IsNullOrWhiteSpace(
                request.Authentication.BearerToken) =>
                "Bearer · Environment Token Session",
            AuthenticationKind.BearerToken => "Bearer · Request Secret",
            AuthenticationKind.Basic => "Basic Auth",
            AuthenticationKind.ApiKey when request.Authentication.ApiKeyLocation == ApiKeyLocation.Header =>
                "Header API Key",
            AuthenticationKind.ApiKey => "Query API Key",
            _ => prepared.AuthenticationSummary
        };
        var usesSession = request.Authentication.Kind == AuthenticationKind.BearerToken &&
                          string.IsNullOrWhiteSpace(request.Authentication.BearerToken);
        var tokenExists = tokenSession?.HasAccessToken == true;
        var expired = tokenSession?.IsExpired(now ?? DateTimeOffset.Now) == true;
        var refreshConfigured = project.RefreshRequestNodeId is Guid refreshId &&
                                ProjectOperations.FindNode(project, refreshId)?.Request is not null;
        var tokenStatus = !usesSession
            ? "此请求不使用 Environment Token Session"
            : !tokenExists
                ? "Token 未设置"
                : expired
                    ? "Token 已过期"
                    : tokenSession?.ExpiresAt is DateTimeOffset expiresAt
                        ? $"Token 有效至 {expiresAt.LocalDateTime:g}"
                        : "Token 已设置，未提供过期时间";
        return new InspectorAuthenticationSummary(
            method,
            usesSession ? "Environment · Token Session" : "Request · Authentication",
            tokenExists,
            expired,
            refreshConfigured,
            tokenStatus);
    }

    private static bool HasRelevantError(
        RouteCompositionPartKind kind,
        string template,
        PreparedRequestResult prepared) =>
        prepared.Validation.Issues.Any(issue => IsRelevant(kind, issue.Target)) ||
        prepared.VariableResolution.Issues.Any(issue =>
            !string.IsNullOrWhiteSpace(issue.VariableName) &&
            TemplateReferences(template, issue.VariableName)) ||
        (template.Contains("{{", StringComparison.Ordinal) &&
         prepared.Validation.Issues.Any(issue => issue.Target == ValidationTarget.Variables));

    private static string RelevantError(
        RouteCompositionPartKind kind,
        string template,
        PreparedRequestResult prepared) =>
        prepared.Validation.Issues.FirstOrDefault(issue => IsRelevant(kind, issue.Target))?.Message ??
        prepared.VariableResolution.Issues.FirstOrDefault(issue =>
            !string.IsNullOrWhiteSpace(issue.VariableName) &&
            TemplateReferences(template, issue.VariableName))?.Message ??
        "此片段包含未解析的变量。";

    private static bool IsRelevant(RouteCompositionPartKind kind, ValidationTarget target) => kind switch
    {
        RouteCompositionPartKind.Environment =>
            target is ValidationTarget.Environment or ValidationTarget.BaseUrl,
        RouteCompositionPartKind.ProjectRoutePart => target == ValidationTarget.ProjectRoute,
        RouteCompositionPartKind.GroupPrefix => target == ValidationTarget.GroupRoute,
        RouteCompositionPartKind.EndpointRoute => target == ValidationTarget.Route,
        RouteCompositionPartKind.PathParameter => target == ValidationTarget.PathParameters,
        RouteCompositionPartKind.QueryParameter => target == ValidationTarget.QueryParameters,
        _ => false
    };

    private static bool TemplateReferences(string template, string variableName) =>
        PlaceholderPattern().Matches(template ?? string.Empty)
            .Any(match => string.Equals(
                match.Groups["name"].Value,
                variableName,
                StringComparison.OrdinalIgnoreCase));

    private static void AddReferences(string template, ISet<string> names)
    {
        foreach (Match match in PlaceholderPattern().Matches(template ?? string.Empty))
        {
            names.Add(match.Groups["name"].Value);
        }
    }

    private static string NormalizeMissing(string value) =>
        string.Equals(value, "未配置", StringComparison.Ordinal) ? string.Empty : value;

    private static string Encode(string value)
    {
        try
        {
            return Uri.EscapeDataString(value ?? string.Empty);
        }
        catch (UriFormatException)
        {
            return Encoding.UTF8.GetBytes(value ?? string.Empty)
                .Aggregate(new StringBuilder(), (builder, item) => builder.Append($"%{item:X2}"))
                .ToString();
        }
    }

    [GeneratedRegex(@"\{\{\s*(?<name>[A-Za-z_][A-Za-z0-9_.-]*)\s*\}\}")]
    private static partial Regex PlaceholderPattern();

    private sealed class SourceCursor(IReadOnlyList<UrlSourcePart> sources)
    {
        private int _index;

        public UrlSourcePart? Next(UrlSourceKind kind)
        {
            while (_index < sources.Count)
            {
                var source = sources[_index++];
                if (source.Kind == kind)
                {
                    return source;
                }
            }
            return null;
        }
    }
}

public sealed record InspectorLayoutState(bool IsExpanded, double Width)
{
    public const double DefaultWidth = 360;
    public const double MinimumWidth = 300;
    public const double MaximumWidth = 520;
    public const double CollapsedWidth = 44;
    public const double MinimumEditorWidth = 620;
    public const double SplitterWidth = 5;

    public InspectorLayoutState Normalize() =>
        this with { Width = Math.Clamp(Width, MinimumWidth, MaximumWidth) };

    public InspectorLayoutDecision Decide(double availableWorkspaceWidth)
    {
        var normalized = Normalize();
        var canExpand =
            availableWorkspaceWidth >= normalized.Width + SplitterWidth + MinimumEditorWidth;
        return new InspectorLayoutDecision(
            normalized.IsExpanded && canExpand,
            canExpand,
            normalized.IsExpanded && canExpand ? normalized.Width : CollapsedWidth);
    }

    public InspectorLayoutState Resize(double horizontalDelta, double availableWorkspaceWidth)
    {
        var availableInspectorWidth = Math.Max(
            MinimumWidth,
            availableWorkspaceWidth - MinimumEditorWidth - SplitterWidth);
        return this with
        {
            Width = Math.Clamp(
                Width - horizontalDelta,
                MinimumWidth,
                Math.Min(MaximumWidth, availableInspectorWidth))
        };
    }
}

public sealed record InspectorLayoutDecision(
    bool IsExpanded,
    bool CanExpand,
    double Width);
