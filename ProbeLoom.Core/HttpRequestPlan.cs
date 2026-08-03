namespace ProbeLoom.Core;

public sealed record HttpHeaderValue(string Name, string Value);

public sealed record HttpRequestPlan(
    Guid RequestNodeId,
    string RequestName,
    string Method,
    Uri Uri,
    IReadOnlyList<HttpHeaderValue> Headers,
    string Body,
    TimeSpan Timeout,
    string? SafeDisplayUrl = null)
{
    public string DisplayUrl => SafeDisplayUrl ?? Uri.AbsoluteUri;

    public IReadOnlyList<HttpHeaderValue> GetEffectiveHeaders()
    {
        var headers = Headers.ToList();
        var bodyBytes = System.Text.Encoding.UTF8.GetByteCount(Body);
        var hasContentHeaders = headers.Any(header =>
            header.Name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(Body) || hasContentHeaders)
        {
            if (!headers.Any(header =>
                    string.Equals(header.Name, "Content-Type", StringComparison.OrdinalIgnoreCase)))
            {
                headers.Add(new HttpHeaderValue("Content-Type", "application/json; charset=utf-8"));
            }

            if (!headers.Any(header =>
                    string.Equals(header.Name, "Content-Length", StringComparison.OrdinalIgnoreCase)))
            {
                headers.Add(new HttpHeaderValue("Content-Length", bodyBytes.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)));
            }
        }

        return headers;
    }
}

public sealed record HttpRequestPlanResult(
    RequestValidationResult Validation,
    HttpRequestPlan? Plan)
{
    public bool Succeeded => Plan is not null && Validation.IsValid;
}

public static class HttpRequestPlanner
{
    public static HttpRequestPlanResult Create(
        ProjectDocument project,
        ProjectEnvironment? environment,
        ProjectNode node,
        TimeSpan timeout)
    {
        var validation = RequestValidator.Validate(project, environment, node);
        if (!validation.IsValid || validation.UrlBreakdown.FinalUri is not Uri uri)
        {
            return new HttpRequestPlanResult(validation, null);
        }

        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(10))
        {
            var issues = validation.Issues
                .Append(new RequestValidationIssue(
                    ValidationTarget.Timeout,
                    "请求超时必须大于 0 秒且不超过 10 分钟。"))
                .ToArray();
            return new HttpRequestPlanResult(validation with { IsValid = false, Issues = issues }, null);
        }

        var request = node.Request!;
        var headers = request.Headers
            .Where(field => field.IsEnabled && !string.IsNullOrWhiteSpace(field.Name))
            .Select(field => new HttpHeaderValue(field.Name.Trim(), field.Value))
            .ToArray();
        return new HttpRequestPlanResult(
            validation,
            new HttpRequestPlan(
                node.Id,
                node.Name,
                request.Method.Trim().ToUpperInvariant(),
                uri,
                headers,
                request.RawJsonBody,
                timeout));
    }
}
