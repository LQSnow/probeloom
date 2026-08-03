using System.Text;

namespace ProbeLoom.Core;

public sealed record ResponseBodyResult(
    HttpResponseContentKind Kind,
    string DisplayBody,
    string RawBody);

public static class ResponseBodyFormatter
{
    public static ResponseBodyResult Format(byte[] bytes, string? mediaType, string? charset)
    {
        if (bytes.Length == 0)
        {
            return new ResponseBodyResult(HttpResponseContentKind.Empty, "响应没有 Body。", string.Empty);
        }

        var normalizedMediaType = mediaType?.Trim().ToLowerInvariant() ?? string.Empty;
        var isDeclaredBinary = normalizedMediaType.StartsWith("image/", StringComparison.Ordinal) ||
                               normalizedMediaType.StartsWith("audio/", StringComparison.Ordinal) ||
                               normalizedMediaType.StartsWith("video/", StringComparison.Ordinal) ||
                               normalizedMediaType is "application/octet-stream" or "application/pdf" or
                                   "application/zip" or "application/gzip";
        var isDeclaredText = normalizedMediaType.StartsWith("text/", StringComparison.Ordinal) ||
                             normalizedMediaType.Contains("json", StringComparison.Ordinal) ||
                             normalizedMediaType.Contains("xml", StringComparison.Ordinal) ||
                             normalizedMediaType.Contains("javascript", StringComparison.Ordinal) ||
                             normalizedMediaType.Contains("x-www-form-urlencoded", StringComparison.Ordinal);
        var looksLikeText = IsProbablyText(bytes);
        if (isDeclaredBinary || (!isDeclaredText && !looksLikeText))
        {
            return new ResponseBodyResult(
                HttpResponseContentKind.Binary,
                BuildBinaryPreview(bytes),
                Convert.ToHexString(bytes));
        }

        var text = Decode(bytes, charset);
        var trimmed = text.TrimStart();
        var isJson = normalizedMediaType.Contains("json", StringComparison.Ordinal) ||
                     trimmed.StartsWith('{') ||
                     trimmed.StartsWith('[');
        if (isJson)
        {
            var formatted = JsonBodyFormatter.Format(text);
            if (formatted.Succeeded)
            {
                return new ResponseBodyResult(HttpResponseContentKind.Json, formatted.FormattedJson, text);
            }
        }

        var kind = normalizedMediaType.Contains("html", StringComparison.Ordinal)
            ? HttpResponseContentKind.Html
            : HttpResponseContentKind.Text;
        return new ResponseBodyResult(kind, text, text);
    }

    private static string Decode(byte[] bytes, string? charset)
    {
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try
            {
                return Encoding.GetEncoding(charset.Trim('"')).GetString(bytes);
            }
            catch (ArgumentException)
            {
                // Fall back to BOM-aware UTF-8 below.
            }
        }

        return new UTF8Encoding(false, false).GetString(bytes);
    }

    private static bool IsProbablyText(byte[] bytes)
    {
        var sampleLength = Math.Min(bytes.Length, 512);
        var controlCharacters = 0;
        for (var index = 0; index < sampleLength; index++)
        {
            var value = bytes[index];
            if (value == 0)
            {
                return false;
            }

            if (value < 0x09 || value is > 0x0D and < 0x20)
            {
                controlCharacters++;
            }
        }

        return controlCharacters <= sampleLength / 20;
    }

    private static string BuildBinaryPreview(byte[] bytes)
    {
        var previewLength = Math.Min(bytes.Length, 512);
        var lines = new List<string>
        {
            $"二进制响应（{bytes.Length:N0} bytes）",
            "以下为前 512 bytes 以内的十六进制预览：",
            string.Empty
        };
        for (var offset = 0; offset < previewLength; offset += 16)
        {
            var count = Math.Min(16, previewLength - offset);
            var hex = string.Join(" ", bytes.Skip(offset).Take(count).Select(value => value.ToString("X2")));
            lines.Add($"{offset:X8}  {hex}");
        }

        return string.Join(Environment.NewLine, lines);
    }
}
