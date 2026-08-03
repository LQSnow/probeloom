using System.Text.Json;

namespace ProbeLoom.Core;

public sealed record JsonFormatResult(bool Succeeded, string FormattedJson, string? Error);

public static class JsonBodyFormatter
{
    public static JsonFormatResult Format(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return new JsonFormatResult(true, string.Empty, null);
        }

        try
        {
            using var document = JsonDocument.Parse(rawJson);
            var formatted = JsonSerializer.Serialize(
                document.RootElement,
                new JsonSerializerOptions { WriteIndented = true });
            return new JsonFormatResult(true, formatted, null);
        }
        catch (JsonException exception)
        {
            return new JsonFormatResult(
                false,
                rawJson,
                $"JSON 第 {exception.LineNumber + 1} 行、第 {exception.BytePositionInLine + 1} 个字符附近无效。");
        }
    }
}
