namespace ProbeLoom.Core;

public sealed record TextEditorState(
    string Text,
    int SelectionStart,
    int SelectionLength);

public sealed record JsonEditorCompletion(
    string Id,
    string Label,
    string Description,
    string InsertText,
    int SelectionOffset,
    int SelectionLength);

public static class JsonEditorAssist
{
    private static readonly IReadOnlyList<JsonEditorCompletion> ValueCompletions =
    [
        new("object", "{ }", "插入 JSON object", "{}", 1, 0),
        new("array", "[ ]", "插入 JSON array", "[]", 1, 0),
        new("string", "\" \"", "插入字符串", "\"\"", 1, 0),
        new("true", "true", "插入布尔值 true", "true", 4, 0),
        new("false", "false", "插入布尔值 false", "false", 5, 0),
        new("null", "null", "插入 null", "null", 4, 0)
    ];

    private static readonly JsonEditorCompletion PropertyCompletion =
        new("property", "\"property\": value", "插入属性模板", "\"property\": null", 1, 8);

    public static TextEditorState? InsertCharacter(
        string? text,
        int selectionStart,
        int selectionLength,
        char character)
    {
        var state = Normalize(text, selectionStart, selectionLength);
        if (character == '"' &&
            state.SelectionLength == 0 &&
            state.SelectionStart < state.Text.Length &&
            state.Text[state.SelectionStart] == '"')
        {
            return state with { SelectionStart = state.SelectionStart + 1 };
        }

        var closing = character switch
        {
            '{' => '}',
            '[' => ']',
            '"' => '"',
            _ => '\0'
        };

        if (closing != '\0')
        {
            var selected = state.Text.Substring(state.SelectionStart, state.SelectionLength);
            var replacement = $"{character}{selected}{closing}";
            var result = ReplaceSelection(state, replacement);
            return result with
            {
                SelectionStart = state.SelectionStart + 1,
                SelectionLength = state.SelectionLength
            };
        }

        if ((character == '}' || character == ']') &&
            state.SelectionLength == 0 &&
            state.SelectionStart < state.Text.Length &&
            state.Text[state.SelectionStart] == character)
        {
            return state with { SelectionStart = state.SelectionStart + 1 };
        }

        return null;
    }

    public static TextEditorState? CompleteAlreadyInsertedCharacter(
        string? text,
        int selectionStart,
        int selectionLength,
        char character)
    {
        var state = Normalize(text, selectionStart, selectionLength);
        if (state.SelectionLength != 0 || state.SelectionStart == 0 ||
            state.Text[state.SelectionStart - 1] != character)
        {
            return null;
        }

        var closing = character switch
        {
            '{' => '}',
            '[' => ']',
            _ => '\0'
        };
        if (character == '"')
        {
            var quoteIndex = state.SelectionStart - 1;
            if (IsClosingQuote(state.Text, quoteIndex))
            {
                var source = state.Text;
                if (state.SelectionStart < source.Length && source[state.SelectionStart] == '"')
                {
                    source = source.Remove(state.SelectionStart, 1);
                }

                var caret = Math.Min(state.SelectionStart, source.Length);
                if (IsPropertyName(source, quoteIndex))
                {
                    source = source.Insert(caret, ": ");
                    caret += 2;
                }

                return new TextEditorState(source, caret, 0);
            }

            return new TextEditorState(
                state.Text.Insert(state.SelectionStart, "\""),
                state.SelectionStart,
                0);
        }
        if (closing == '\0')
        {
            return null;
        }

        return new TextEditorState(
            state.Text.Insert(state.SelectionStart, closing.ToString()),
            state.SelectionStart,
            0);
    }

    public static TextEditorState? CompleteExistingClosingQuote(string? text, int selectionStart)
    {
        var state = Normalize(text, selectionStart, 0);
        if (state.SelectionStart >= state.Text.Length ||
            state.Text[state.SelectionStart] != '"' ||
            !IsClosingQuote(state.Text, state.SelectionStart) ||
            !IsPropertyName(state.Text, state.SelectionStart))
        {
            return null;
        }

        var insertionIndex = state.SelectionStart + 1;
        return new TextEditorState(
            state.Text.Insert(insertionIndex, ": "),
            insertionIndex + 2,
            0);
    }

    private static bool IsClosingQuote(string text, int quoteIndex)
    {
        var unescapedQuotes = 0;
        for (var index = 0; index < quoteIndex; index++)
        {
            if (text[index] == '"' && !IsEscaped(text, index))
            {
                unescapedQuotes++;
            }
        }

        return unescapedQuotes % 2 == 1;
    }

    private static bool IsPropertyName(string text, int closingQuoteIndex)
    {
        var openingQuote = closingQuoteIndex - 1;
        while (openingQuote >= 0)
        {
            if (text[openingQuote] == '"' && !IsEscaped(text, openingQuote))
            {
                break;
            }
            openingQuote--;
        }
        if (openingQuote <= 0)
        {
            return false;
        }

        for (var index = openingQuote - 1; index >= 0; index--)
        {
            if (char.IsWhiteSpace(text[index]))
            {
                continue;
            }
            return text[index] is '{' or ',';
        }
        return true;
    }

    private static bool IsEscaped(string text, int index)
    {
        var slashCount = 0;
        for (var cursor = index - 1; cursor >= 0 && text[cursor] == '\\'; cursor--)
        {
            slashCount++;
        }
        return slashCount % 2 == 1;
    }

    public static TextEditorState InsertNewLine(
        string? text,
        int selectionStart,
        int selectionLength,
        string indentation = "  ")
    {
        var state = Normalize(text, selectionStart, selectionLength);
        var before = state.Text[..state.SelectionStart];
        var lineStart = before.LastIndexOf('\n') + 1;
        var line = before[lineStart..].TrimEnd('\r');
        var leadingWhitespace = new string(line.TakeWhile(char.IsWhiteSpace).ToArray());
        var trimmedBefore = before.TrimEnd();
        var previous = trimmedBefore.Length > 0 ? trimmedBefore[^1] : '\0';
        var next = state.SelectionStart < state.Text.Length
            ? state.Text[state.SelectionStart]
            : '\0';
        var opensBlock = previous is '{' or '[';
        var isBetweenPair =
            (previous == '{' && next == '}') ||
            (previous == '[' && next == ']');
        var appendComma = ShouldAppendComma(state.Text, state.SelectionStart, previous);

        var innerIndent = leadingWhitespace + (opensBlock ? indentation : string.Empty);
        var insertion = (appendComma ? "," : string.Empty) + Environment.NewLine + innerIndent;
        if (isBetweenPair)
        {
            insertion += Environment.NewLine + leadingWhitespace;
        }

        var result = ReplaceSelection(state, insertion);
        return result with
        {
            SelectionStart = state.SelectionStart + (appendComma ? 1 : 0) + Environment.NewLine.Length + innerIndent.Length,
            SelectionLength = 0
        };
    }

    public static TextEditorState InsertIndentation(
        string? text,
        int selectionStart,
        int selectionLength,
        string indentation = "  ")
    {
        var state = Normalize(text, selectionStart, selectionLength);
        if (state.SelectionLength == 0)
        {
            return ReplaceSelection(state, indentation);
        }

        var firstLineStart = state.Text.LastIndexOf('\n', Math.Max(0, state.SelectionStart - 1)) + 1;
        var selectionEnd = state.SelectionStart + state.SelectionLength;
        var selectedLines = state.Text[firstLineStart..selectionEnd]
            .Replace("\n", "\n" + indentation, StringComparison.Ordinal);
        var replacement = indentation + selectedLines;
        var textResult = state.Text.Remove(firstLineStart, selectionEnd - firstLineStart)
            .Insert(firstLineStart, replacement);
        var added = replacement.Length - (selectionEnd - firstLineStart);
        return new TextEditorState(
            textResult,
            state.SelectionStart + indentation.Length,
            state.SelectionLength + added);
    }

    public static int MapCaretIndex(string? sourceText, int sourceIndex, string? targetText)
    {
        var source = sourceText ?? string.Empty;
        var target = targetText ?? string.Empty;
        var clamped = Math.Clamp(sourceIndex, 0, source.Length);
        if (string.Equals(source, target, StringComparison.Ordinal))
        {
            return clamped;
        }

        var logicalOffset = 0;
        for (var index = 0; index < clamped; index++)
        {
            if (source[index] == '\r' && index + 1 < clamped && source[index + 1] == '\n')
            {
                index++;
            }
            logicalOffset++;
        }

        var targetIndex = 0;
        for (var offset = 0; offset < logicalOffset && targetIndex < target.Length; offset++, targetIndex++)
        {
            if (target[targetIndex] == '\r' && targetIndex + 1 < target.Length && target[targetIndex + 1] == '\n')
            {
                targetIndex++;
            }
        }
        return Math.Clamp(targetIndex, 0, target.Length);
    }

    private static bool ShouldAppendComma(string text, int caret, char previous)
    {
        var nextSignificant = text[caret..].FirstOrDefault(character => !char.IsWhiteSpace(character));
        if (previous is '\0' or ',' or ':' or '{' or '[' || nextSignificant is not ('}' or ']'))
        {
            return false;
        }

        var lineStart = text.LastIndexOf('\n', Math.Max(0, caret - 1)) + 1;
        var currentLine = text[lineStart..caret].Trim();
        if (currentLine.Length == 0)
        {
            return false;
        }

        var stack = new Stack<char>();
        var inString = false;
        for (var index = 0; index < caret; index++)
        {
            var character = text[index];
            if (character == '"' && !IsEscaped(text, index))
            {
                inString = !inString;
                continue;
            }
            if (inString)
            {
                continue;
            }
            if (character is '{' or '[')
            {
                stack.Push(character);
            }
            else if (character is '}' or ']' && stack.Count > 0)
            {
                stack.Pop();
            }
        }

        if (stack.Count == 0)
        {
            return false;
        }
        return stack.Peek() == '[' || currentLine.Contains(':', StringComparison.Ordinal);
    }

    public static TextEditorState? BackspacePair(
        string? text,
        int selectionStart,
        int selectionLength)
    {
        var state = Normalize(text, selectionStart, selectionLength);
        if (state.SelectionLength != 0 ||
            state.SelectionStart == 0 ||
            state.SelectionStart >= state.Text.Length)
        {
            return null;
        }

        var pair = $"{state.Text[state.SelectionStart - 1]}{state.Text[state.SelectionStart]}";
        if (pair is not ("{}" or "[]" or "\"\""))
        {
            return null;
        }

        return new TextEditorState(
            state.Text.Remove(state.SelectionStart - 1, 2),
            state.SelectionStart - 1,
            0);
    }

    public static IReadOnlyList<JsonEditorCompletion> GetCompletions(
        string? text,
        int caretIndex)
    {
        var source = text ?? string.Empty;
        var caret = Math.Clamp(caretIndex, 0, source.Length);
        var previous = source[..caret].TrimEnd();
        var expectsProperty = previous.Length == 0 ||
                              previous[^1] is '{' or ',';

        return expectsProperty
            ? [PropertyCompletion, .. ValueCompletions]
            : ValueCompletions;
    }

    public static TextEditorState ApplyCompletion(
        string? text,
        int selectionStart,
        int selectionLength,
        JsonEditorCompletion completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        var state = Normalize(text, selectionStart, selectionLength);
        var result = ReplaceSelection(state, completion.InsertText);
        return result with
        {
            SelectionStart = state.SelectionStart + completion.SelectionOffset,
            SelectionLength = completion.SelectionLength
        };
    }

    private static TextEditorState ReplaceSelection(
        TextEditorState state,
        string replacement) =>
        new(
            state.Text.Remove(state.SelectionStart, state.SelectionLength)
                .Insert(state.SelectionStart, replacement),
            state.SelectionStart + replacement.Length,
            0);

    private static TextEditorState Normalize(
        string? text,
        int selectionStart,
        int selectionLength)
    {
        var source = text ?? string.Empty;
        var start = Math.Clamp(selectionStart, 0, source.Length);
        var length = Math.Clamp(selectionLength, 0, source.Length - start);
        return new TextEditorState(source, start, length);
    }
}

public sealed class TextEditHistory(int capacity = 100)
{
    private readonly int _capacity = Math.Max(1, capacity);
    private readonly List<TextEditorState> _undo = [];
    private readonly List<TextEditorState> _redo = [];

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public void Reset()
    {
        _undo.Clear();
        _redo.Clear();
    }

    public void Record(TextEditorState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (_undo.Count > 0 && _undo[^1] == state)
        {
            return;
        }

        _undo.Add(state);
        if (_undo.Count > _capacity)
        {
            _undo.RemoveAt(0);
        }

        _redo.Clear();
    }

    public bool TryUndo(TextEditorState current, out TextEditorState state)
    {
        if (_undo.Count == 0)
        {
            state = current;
            return false;
        }

        state = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(current);
        return true;
    }

    public bool TryRedo(TextEditorState current, out TextEditorState state)
    {
        if (_redo.Count == 0)
        {
            state = current;
            return false;
        }

        state = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(current);
        return true;
    }
}
