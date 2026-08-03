using System.Text.RegularExpressions;

namespace ProbeLoom.Core;

public sealed record VariableReferenceInsertion(
    bool Succeeded,
    string Text,
    int CaretIndex,
    string Error);

public static partial class VariableReference
{
    public static bool ContainsReference(string? text) =>
        ReferencePattern().IsMatch(text ?? string.Empty);

    public static bool TryGetAt(string? text, int position, out string name)
    {
        var source = text ?? string.Empty;
        var index = Math.Clamp(position, 0, source.Length);
        foreach (Match match in ReferencePattern().Matches(source))
        {
            if (index >= match.Index && index <= match.Index + match.Length)
            {
                name = match.Groups["name"].Value;
                return true;
            }
        }

        name = string.Empty;
        return false;
    }

    public static string Format(string name)
    {
        var normalized = (name ?? string.Empty).Trim();
        return IsValidName(normalized) ? $"{{{{{normalized}}}}}" : string.Empty;
    }

    public static VariableReferenceInsertion Insert(
        string? text,
        int selectionStart,
        int selectionLength,
        string variableName)
    {
        var reference = Format(variableName);
        if (reference.Length == 0)
        {
            return new VariableReferenceInsertion(
                false,
                text ?? string.Empty,
                Math.Clamp(selectionStart, 0, (text ?? string.Empty).Length),
                "变量名称无效。");
        }

        var source = text ?? string.Empty;
        var start = Math.Clamp(selectionStart, 0, source.Length);
        var length = Math.Clamp(selectionLength, 0, source.Length - start);
        var result = source.Remove(start, length).Insert(start, reference);
        return new VariableReferenceInsertion(
            true,
            result,
            start + reference.Length,
            string.Empty);
    }

    public static bool IsValidName(string name) =>
        !string.IsNullOrWhiteSpace(name) && VariableNamePattern().IsMatch(name.Trim());

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_.-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex VariableNamePattern();

    [GeneratedRegex(@"\{\{\s*(?<name>[A-Za-z_][A-Za-z0-9_.-]*)\s*\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex ReferencePattern();
}
