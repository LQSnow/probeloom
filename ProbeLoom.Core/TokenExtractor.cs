using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ProbeLoom.Core;

public sealed record TokenExtractionResult(
    bool Succeeded,
    TokenSession? Session,
    string? Error)
{
    public static TokenExtractionResult Failure(string error) => new(false, null, error);

    public static TokenExtractionResult Success(TokenSession session) => new(true, session, null);
}

public static partial class TokenExtractor
{
    public static TokenExtractionResult Extract(
        string json,
        TokenCaptureConfiguration configuration,
        TokenSession? previousSession = null,
        DateTimeOffset? now = null)
    {
        if (!configuration.IsEnabled)
        {
            return TokenExtractionResult.Failure("当前请求未启用 Token Capture。");
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!TryRead(root, configuration.AccessTokenPath, out var accessElement))
            {
                return TokenExtractionResult.Failure(
                    $"响应中找不到 Access Token 路径“{configuration.AccessTokenPath}”。");
            }

            var accessToken = ElementAsString(accessElement);
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                return TokenExtractionResult.Failure("提取到的 Access Token 为空。");
            }

            var refreshToken = previousSession?.RefreshToken ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(configuration.RefreshTokenPath) &&
                TryRead(root, configuration.RefreshTokenPath, out var refreshElement))
            {
                refreshToken = ElementAsString(refreshElement);
            }

            var currentTime = now ?? DateTimeOffset.Now;
            DateTimeOffset? expiresAt = null;
            if (!string.IsNullOrWhiteSpace(configuration.ExpiresAtPath) &&
                TryRead(root, configuration.ExpiresAtPath, out var expiresAtElement))
            {
                expiresAt = ParseExpiry(expiresAtElement);
                if (expiresAt is null)
                {
                    return TokenExtractionResult.Failure(
                        $"过期时间路径“{configuration.ExpiresAtPath}”不是 ISO 时间或 Unix 时间戳。");
                }
            }
            else if (!string.IsNullOrWhiteSpace(configuration.ExpiresInPath) &&
                     TryRead(root, configuration.ExpiresInPath, out var expiresInElement))
            {
                if (!TryReadDouble(expiresInElement, out var seconds) || seconds < 0)
                {
                    return TokenExtractionResult.Failure(
                        $"有效期路径“{configuration.ExpiresInPath}”必须是非负秒数。");
                }
                expiresAt = currentTime.AddSeconds(seconds);
            }

            return TokenExtractionResult.Success(new TokenSession(
                accessToken,
                refreshToken,
                expiresAt,
                currentTime));
        }
        catch (JsonException exception)
        {
            return TokenExtractionResult.Failure($"Token 响应不是有效 JSON：{exception.Message}");
        }
    }

    public static bool TryRead(JsonElement root, string path, out JsonElement value)
    {
        value = root;
        if (string.IsNullOrWhiteSpace(path) || path.Trim() == "$")
        {
            return true;
        }

        var normalized = path.Trim();
        if (normalized.StartsWith('$'))
        {
            normalized = normalized[1..];
        }
        if (normalized.StartsWith('.'))
        {
            normalized = normalized[1..];
        }

        foreach (Match match in PathPartPattern().Matches(normalized))
        {
            if (match.Groups["property"].Success)
            {
                if (value.ValueKind != JsonValueKind.Object ||
                    !value.TryGetProperty(match.Groups["property"].Value, out value))
                {
                    return false;
                }
            }
            else
            {
                if (value.ValueKind != JsonValueKind.Array ||
                    !int.TryParse(match.Groups["index"].Value, out var index) ||
                    index < 0 ||
                    index >= value.GetArrayLength())
                {
                    return false;
                }
                value = value[index];
            }
        }

        var consumed = string.Concat(PathPartPattern().Matches(normalized).Select(match => match.Value));
        return string.Equals(consumed, normalized, StringComparison.Ordinal);
    }

    private static string ElementAsString(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? string.Empty,
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => element.GetRawText(),
        _ => string.Empty
    };

    private static DateTimeOffset? ParseExpiry(JsonElement element)
    {
        if (TryReadDouble(element, out var unix))
        {
            try
            {
                return unix > 10_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds((long)unix)
                    : DateTimeOffset.FromUnixTimeSeconds((long)unix);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        return element.ValueKind == JsonValueKind.String &&
               DateTimeOffset.TryParse(
                   element.GetString(),
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.RoundtripKind,
                   out var parsed)
            ? parsed
            : null;
    }

    private static bool TryReadDouble(JsonElement element, out double value)
    {
        value = 0;
        if (element.ValueKind == JsonValueKind.Number)
        {
            return element.TryGetDouble(out value);
        }

        return element.ValueKind == JsonValueKind.String &&
               double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    [GeneratedRegex(@"(?:(?<property>[A-Za-z_][A-Za-z0-9_-]*)|\[(?<index>\d+)\])(?:\.|(?=\[)|$)")]
    private static partial Regex PathPartPattern();
}
