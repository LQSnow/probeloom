namespace ProbeLoom.Core;

public enum HttpExecutionState
{
    Succeeded,
    Failed,
    TimedOut,
    Cancelled
}

public enum HttpErrorKind
{
    None,
    InvalidRequest,
    Timeout,
    Cancelled,
    Connection,
    Tls,
    Protocol,
    Redirect,
    Other
}

public enum HttpResponseContentKind
{
    Empty,
    Json,
    Text,
    Html,
    Binary
}

public sealed record HttpExecutionResult(
    Guid Id,
    Guid RequestNodeId,
    string RequestName,
    string Method,
    string Url,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    HttpExecutionState State,
    int? StatusCode,
    string ReasonPhrase,
    IReadOnlyList<HttpHeaderValue> ResponseHeaders,
    long ResponseSizeBytes,
    string ContentType,
    HttpResponseContentKind ContentKind,
    string DisplayBody,
    string RawBody,
    bool IsBodyTruncated,
    HttpErrorKind ErrorKind,
    string ErrorTitle,
    string ErrorDetail)
{
    public bool IsSuccessStatusCode => StatusCode is >= 200 and <= 299;

    public string FinalUrl { get; init; } = Url;

    public IReadOnlyList<HttpRedirectHop> RedirectChain { get; init; } = [];

    public HttpPhaseTiming Timing { get; init; } =
        new(StartedAt, null, null, Duration);
}

public sealed record HttpRedirectHop(
    int Sequence,
    int StatusCode,
    string Url,
    string Location,
    string TargetUrl,
    TimeSpan Duration,
    bool SensitiveHeadersRemoved);

public sealed record HttpPhaseTiming(
    DateTimeOffset StartedAt,
    TimeSpan? HeadersReceived,
    TimeSpan? FirstByte,
    TimeSpan Total);
