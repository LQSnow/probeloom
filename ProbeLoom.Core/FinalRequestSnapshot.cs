using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProbeLoom.Core;

public sealed record InspectedRequestValue(
    string Name,
    string Value,
    string Source,
    bool IsSensitive);

public sealed record FinalRequestSnapshot(
    Guid RequestNodeId,
    string Method,
    string Url,
    IReadOnlyList<InspectedRequestValue> Headers,
    string Body,
    string Authentication,
    TimeSpan Timeout,
    string ContentType,
    long ContentLength,
    bool BodyContainsMaskedValues)
{
    public bool HasBody => ContentLength > 0;
}

public static class FinalRequestSnapshotFactory
{
    public static FinalRequestSnapshot? Create(PreparedRequestResult prepared)
    {
        if (prepared.Plan is null || prepared.SafePlan is null)
        {
            return null;
        }

        var actualHeaders = prepared.Plan.GetEffectiveHeaders();
        var safeHeaders = prepared.SafePlan.GetEffectiveHeaders();
        var headers = actualHeaders.Select((header, index) =>
        {
            var safeValue = index < safeHeaders.Count &&
                            string.Equals(header.Name, safeHeaders[index].Name, StringComparison.OrdinalIgnoreCase)
                ? safeHeaders[index].Value
                : SensitiveDataMasker.MaskHeader(header.Name, header.Value);
            if (string.Equals(header.Name, "Content-Length", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(header.Name, "Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                safeValue = header.Value;
            }
            var sensitive = SensitiveDataMasker.IsSensitiveHeader(header.Name) ||
                            !string.Equals(header.Value, safeValue, StringComparison.Ordinal);
            return new InspectedRequestValue(
                header.Name,
                sensitive ? SensitiveDataMasker.MaskHeader(header.Name, safeValue) : safeValue,
                HeaderSource(prepared, header.Name) +
                (!string.Equals(header.Value, safeValue, StringComparison.Ordinal)
                    ? " · Variable / Secret"
                    : string.Empty),
                sensitive);
        }).ToArray();

        var contentType = headers.FirstOrDefault(header =>
            string.Equals(header.Name, "Content-Type", StringComparison.OrdinalIgnoreCase))?.Value ?? string.Empty;
        var contentLengthHeader = actualHeaders.FirstOrDefault(header =>
            string.Equals(header.Name, "Content-Length", StringComparison.OrdinalIgnoreCase))?.Value;
        var contentLength = long.TryParse(
            contentLengthHeader,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var declaredLength)
            ? declaredLength
            : Encoding.UTF8.GetByteCount(prepared.Plan.Body);
        return new FinalRequestSnapshot(
            prepared.Plan.RequestNodeId,
            prepared.Plan.Method,
            SensitiveDataMasker.MaskUri(prepared.SafePlan.Uri).AbsoluteUri,
            headers,
            SensitiveDataMasker.MaskJsonBody(prepared.SafePlan.Body),
            prepared.AuthenticationSummary,
            prepared.Plan.Timeout,
            contentType,
            contentLength,
            !string.Equals(prepared.Plan.Body, prepared.SafePlan.Body, StringComparison.Ordinal));
    }

    private static string HeaderSource(PreparedRequestResult prepared, string name)
    {
        if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase))
        {
            return "HTTP content";
        }

        return prepared.AuthenticationKind switch
        {
            AuthenticationKind.BearerToken or AuthenticationKind.Basic
                when string.Equals(name, "Authorization", StringComparison.OrdinalIgnoreCase) =>
                $"Authentication · {prepared.AuthenticationSummary}",
            AuthenticationKind.ApiKey
                when !string.IsNullOrWhiteSpace(prepared.AuthenticationHeaderName) &&
                     string.Equals(name, prepared.AuthenticationHeaderName, StringComparison.OrdinalIgnoreCase) =>
                "Authentication / API Key",
            _ => "Request Header"
        };
    }
}

public static class SensitiveDataMasker
{
    public const string Mask = "••••••";

    private static readonly string[] SensitiveNameParts =
    [
        "authorization", "proxy-authorization", "cookie", "set-cookie",
        "token", "api-key", "apikey", "secret", "password"
    ];

    public static bool IsSensitiveHeader(string name) =>
        SensitiveNameParts.Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase));

    public static string MaskHeader(string name, string value) =>
        IsSensitiveHeader(name) ? Mask : value;

    public static string MaskJsonBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return body;
        }

        try
        {
            var node = JsonNode.Parse(body);
            if (node is null)
            {
                return body;
            }

            MaskNode(node);
            return node.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
        }
        catch (JsonException)
        {
            return System.Text.RegularExpressions.Regex.Replace(
                body,
                "(?i)(\"(?:authorization|cookie|token|api[-_]?key|secret|password)[^\"]*\"\\s*:\\s*)\"(?:\\\\.|[^\"])*\"",
                $"$1\"{Mask}\"",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        }
    }

    private static void MaskNode(JsonNode node)
    {
        if (node is JsonObject value)
        {
            foreach (var property in value.ToArray())
            {
                if (SensitiveNameParts.Any(part =>
                        property.Key.Contains(part, StringComparison.OrdinalIgnoreCase)))
                {
                    value[property.Key] = Mask;
                }
                else if (property.Value is not null)
                {
                    MaskNode(property.Value);
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array.Where(item => item is not null))
            {
                MaskNode(child!);
            }
        }
    }

    public static Uri MaskUri(Uri uri)
    {
        if (string.IsNullOrEmpty(uri.Query))
        {
            return uri;
        }

        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(item =>
            {
                var separator = item.IndexOf('=');
                var encodedName = separator < 0 ? item : item[..separator];
                var name = Uri.UnescapeDataString(encodedName.Replace("+", "%20", StringComparison.Ordinal));
                return SensitiveNameParts.Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase))
                    ? $"{encodedName}={Uri.EscapeDataString(Mask)}"
                    : item;
            });
        var builder = new UriBuilder(uri) { Query = string.Join("&", query) };
        return builder.Uri;
    }
}

public sealed record CurlExportResult(bool Succeeded, string Command, string? Error = null);

public static class PowerShellCurlExporter
{
    public const int MaximumInlineBodyBytes = 64 * 1024;

    public static CurlExportResult Export(FinalRequestSnapshot snapshot)
    {
        if (snapshot.ContentLength > MaximumInlineBodyBytes)
        {
            return new CurlExportResult(
                false,
                string.Empty,
                $"Body 超过 {MaximumInlineBodyBytes / 1024} KiB，不能安全地内联到命令中。");
        }

        var lines = new List<string>
        {
            $"curl.exe --request {Quote(snapshot.Method)}",
            $"  --url {Quote(snapshot.Url)}"
        };
        lines.AddRange(snapshot.Headers
            .Where(header => !string.Equals(header.Name, "Content-Length", StringComparison.OrdinalIgnoreCase))
            .Select(header => $"  --header {Quote($"{header.Name}: {header.Value}")}"));
        if (snapshot.HasBody)
        {
            lines.Add($"  --data-raw {Quote(snapshot.Body)}");
        }

        return new CurlExportResult(
            true,
            string.Join(" `\r\n", lines),
            null);
    }

    private static string Quote(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
}
