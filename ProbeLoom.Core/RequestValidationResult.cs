namespace ProbeLoom.Core;

public enum ValidationTarget
{
    Method,
    Timeout,
    Variables,
    Authentication,
    Environment,
    BaseUrl,
    ProjectRoute,
    GroupRoute,
    Route,
    PathParameters,
    QueryParameters,
    Headers,
    RawJsonBody
}

public sealed record RequestValidationIssue(ValidationTarget Target, string Message);

public sealed record RequestValidationResult(
    bool IsValid,
    string FinalUrl,
    RequestUrlBreakdown UrlBreakdown,
    IReadOnlyList<RequestValidationIssue> Issues,
    IReadOnlyList<string> Notes)
{
    public IReadOnlyList<string> Errors => Issues.Select(issue => issue.Message).ToArray();
}
