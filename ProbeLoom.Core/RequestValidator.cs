using System.Text.Json;

namespace ProbeLoom.Core;

public static class RequestValidator
{
    public static readonly IReadOnlySet<string> SupportedMethods =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "GET",
            "POST",
            "PUT",
            "PATCH",
            "DELETE",
            "HEAD",
            "OPTIONS"
        };

    public static RequestValidationResult Validate(
        string method,
        string baseUrl,
        string route,
        IEnumerable<RequestField>? queryParameters,
        IEnumerable<RequestField>? headers,
        string? rawJsonBody)
    {
        var project = new ProjectDocument();
        var environment = new ProjectEnvironment { Name = "当前 Environment", BaseUrl = baseUrl };
        var node = new ProjectNode
        {
            Kind = ProjectNodeKind.Endpoint,
            Name = "当前请求",
            Request = new RequestDefinition
            {
                Method = method,
                Route = route,
                RawJsonBody = rawJsonBody ?? string.Empty,
                QueryParameters = new System.Collections.ObjectModel.ObservableCollection<RequestField>(
                    (queryParameters ?? []).Select(field => field.Clone())),
                Headers = new System.Collections.ObjectModel.ObservableCollection<RequestField>(
                    (headers ?? []).Select(field => field.Clone()))
            }
        };
        return Validate(project, environment, node);
    }

    public static RequestValidationResult Validate(
        ProjectDocument project,
        ProjectEnvironment? environment,
        ProjectNode node)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(node);
        var request = node.Request ?? throw new ArgumentException("节点不包含请求定义。", nameof(node));
        var issues = new List<RequestValidationIssue>();
        var notes = new List<string>();
        var normalizedMethod = request.Method?.Trim() ?? string.Empty;
        var baseUrl = environment?.BaseUrl ?? string.Empty;
        var route = request.Route;
        var queryParameters = request.QueryParameters;
        var headers = request.Headers;
        var rawJsonBody = request.RawJsonBody;
        var breakdown = RequestUrlComposer.ComposeDetailed(project, environment, node);

        if (!SupportedMethods.Contains(normalizedMethod))
        {
            issues.Add(new RequestValidationIssue(ValidationTarget.Method, "请选择受支持的 HTTP Method。"));
        }

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            issues.Add(new RequestValidationIssue(
                ValidationTarget.BaseUrl,
                "当前 Environment 尚未配置 Base URL。"));
        }
        else if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            issues.Add(new RequestValidationIssue(
                ValidationTarget.BaseUrl,
                "Base URL 必须是完整的 HTTP 或 HTTPS 地址，例如 https://api.example.com。"));
        }
        else if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            issues.Add(new RequestValidationIssue(
                ValidationTarget.BaseUrl,
                "Base URL 不应包含 Query 或 Fragment；请将它们放到请求编辑区。"));
        }

        foreach (var part in project.RouteParts.Where(part => part.IsEnabled))
        {
            ValidateRoutePart(
                part.Value,
                ValidationTarget.ProjectRoute,
                $"Project Route Part“{part.Name}”",
                issues,
                allowQuery: false);
        }

        foreach (var group in ProjectOperations.GetAncestors(project, node.Id)
                     .Where(item => item.Kind == ProjectNodeKind.Group && item.IsRoutePrefixEnabled))
        {
            ValidateRoutePart(
                group.RoutePrefix,
                ValidationTarget.GroupRoute,
                $"分组“{group.Name}”的 Route Prefix",
                issues,
                allowQuery: false);
        }

        if (string.IsNullOrWhiteSpace(route))
        {
            issues.Add(new RequestValidationIssue(ValidationTarget.Route, "Route 不能为空。"));
        }
        else if (Uri.TryCreate(route.Trim(), UriKind.Absolute, out _))
        {
            issues.Add(new RequestValidationIssue(
                ValidationTarget.Route,
                "Route 应是相对路径；域名和协议应配置在 Environment 的 Base URL 中。"));
        }
        else if (!route.TrimStart().StartsWith('/'))
        {
            issues.Add(new RequestValidationIssue(
                ValidationTarget.Route,
                "Route 应以“/”开头，例如 /v1/users。"));
        }
        else
        {
            ValidateRoutePart(route, ValidationTarget.Route, "Endpoint Route", issues, allowQuery: true);
        }

        var duplicatePathParameters = request.PathParameters
            .Where(field => field.IsEnabled && !string.IsNullOrWhiteSpace(field.Name))
            .GroupBy(field => field.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicatePathParameters.Length > 0)
        {
            issues.Add(new RequestValidationIssue(
                ValidationTarget.PathParameters,
                $"启用的 Path Parameter 名称重复：{string.Join("、", duplicatePathParameters)}。"));
        }

        if (request.PathParameters.Any(field =>
                field.IsEnabled && string.IsNullOrWhiteSpace(field.Name) && !string.IsNullOrWhiteSpace(field.Value)))
        {
            issues.Add(new RequestValidationIssue(
                ValidationTarget.PathParameters,
                "启用且包含值的 Path Parameter 必须填写名称。"));
        }

        if (breakdown.MissingPathParameters.Count > 0)
        {
            issues.Add(new RequestValidationIssue(
                ValidationTarget.PathParameters,
                $"缺少 Path Parameter：{string.Join("、", breakdown.MissingPathParameters.Select(name => $"{{{name}}}"))}。"));
        }

        var duplicateHeaders = (headers ?? [])
            .Where(field => field.IsEnabled && !string.IsNullOrWhiteSpace(field.Name))
            .GroupBy(field => field.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        if (duplicateHeaders.Length > 0)
        {
            issues.Add(new RequestValidationIssue(
                ValidationTarget.Headers,
                $"启用的 Header 名称重复：{string.Join("、", duplicateHeaders)}。"));
        }

        if ((headers ?? []).Any(field =>
                field.IsEnabled && string.IsNullOrWhiteSpace(field.Name) && !string.IsNullOrWhiteSpace(field.Value)))
        {
            issues.Add(new RequestValidationIssue(
                ValidationTarget.Headers,
                "启用且包含值的 Header 必须填写名称。"));
        }

        var duplicateQueryParameters = (queryParameters ?? [])
            .Where(field => field.IsEnabled && !string.IsNullOrWhiteSpace(field.Name))
            .GroupBy(field => field.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        if (duplicateQueryParameters.Length > 0)
        {
            issues.Add(new RequestValidationIssue(
                ValidationTarget.QueryParameters,
                $"启用的 Query Parameter 名称重复：{string.Join("、", duplicateQueryParameters)}。"));
        }

        if ((queryParameters ?? []).Any(field =>
                field.IsEnabled && string.IsNullOrWhiteSpace(field.Name) && !string.IsNullOrWhiteSpace(field.Value)))
        {
            issues.Add(new RequestValidationIssue(
                ValidationTarget.QueryParameters,
                "启用且包含值的 Query Parameter 必须填写名称。"));
        }

        if (!string.IsNullOrWhiteSpace(rawJsonBody))
        {
            try
            {
                using var _ = JsonDocument.Parse(rawJsonBody);
            }
            catch (JsonException exception)
            {
                issues.Add(new RequestValidationIssue(
                    ValidationTarget.RawJsonBody,
                    $"Raw JSON 在第 {exception.LineNumber + 1} 行、第 {exception.BytePositionInLine + 1} 个字符附近无效。"));
            }

            if (string.Equals(normalizedMethod, "GET", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalizedMethod, "HEAD", StringComparison.OrdinalIgnoreCase))
            {
                notes.Add($"{normalizedMethod.ToUpperInvariant()} 请求通常不携带 Body。");
            }
        }

        if (issues.Count == 0)
        {
            notes.Insert(0, "请求定义校验通过，可以发送。");
        }

        return new RequestValidationResult(
            issues.Count == 0,
            breakdown.FinalUrl,
            breakdown,
            issues,
            notes);
    }

    private static void ValidateRoutePart(
        string? value,
        ValidationTarget target,
        string label,
        ICollection<RequestValidationIssue> issues,
        bool allowQuery)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            issues.Add(new RequestValidationIssue(target, $"{label} 已启用但内容为空。"));
            return;
        }

        var trimmed = value.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out _))
        {
            issues.Add(new RequestValidationIssue(target, $"{label} 必须是相对路径，不能包含协议和域名。"));
            return;
        }

        if (trimmed.Contains('#'))
        {
            issues.Add(new RequestValidationIssue(target, $"{label} 不能包含 Fragment（#）。"));
        }

        if (!allowQuery && trimmed.Contains('?'))
        {
            issues.Add(new RequestValidationIssue(target, $"{label} 不能包含 Query（?）。"));
        }
    }
}
