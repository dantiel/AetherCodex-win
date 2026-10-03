using System.Globalization;

namespace AetherCodex.Util;

/// <summary>
/// A deliberately small YAML subset parser for the ÆtherCodex theme files.
/// Handles the constructs the themes actually use: comments, nested maps by
/// indentation, scalar values (quoted/unquoted/number/bool), and inline
/// <c>[a, b, c]</c> arrays. Not a general YAML implementation.
/// </summary>
public static class SimpleYaml
{
    public static Dictionary<string, object?> Parse(string text)
    {
        var root = new Dictionary<string, object?>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        // Stack of (indent, container). Container is either a map or a list.
        var stack = new List<(int Indent, object Container, string? Key)>();
        stack.Add((-1, root, null));

        foreach (var rawLine in lines)
        {
            var line = StripComment(rawLine);
            if (string.IsNullOrWhiteSpace(line)) continue;

            var indent = CountIndent(line);
            var content = line.Substring(indent);

            if (content.StartsWith('-') && content.Length > 1 && char.IsWhiteSpace(content[1]))
            {
                // Inline list item — append scalar to nearest list parent.
                // (Themes use `[a, b]` inline arrays rather than block lists,
                //  but tolerate this gracefully.)
                continue;
            }

            var colon = FindKeyColon(content);
            if (colon < 0) continue;

            var key = Unquote(content[..colon].Trim());
            var valueText = content[(colon + 1)..].Trim();

            // Pop stack to the current indent level.
            while (stack.Count > 1 && stack[^1].Indent >= indent)
                stack.RemoveAt(stack.Count - 1);

            var parent = stack[^1].Container;

            if (string.IsNullOrEmpty(valueText))
            {
                // Nested map marker (`scopes:` with nothing on the line).
                var child = new Dictionary<string, object?>();
                if (parent is Dictionary<string, object?> pmap) pmap[key] = child;
                else if (parent is List<object?> plist) plist.Add(child);
                stack.Add((indent, child, key));
            }
            else
            {
                var value = ParseScalar(valueText);
                if (parent is Dictionary<string, object?> map)
                {
                    // A value may itself be a nested map shorthand
                    // (`comment: { color: X, italic: true }`).
                    map[key] = value;
                }
                else if (parent is List<object?> list)
                {
                    list.Add(value);
                }
                // Do not push scalar values onto the stack — they cannot parent.
            }
        }

        return root;
    }

    private static object? ParseScalar(string text)
    {
        if (text.StartsWith('[') && text.EndsWith(']'))
            return ParseInlineArray(text);

        if (text.StartsWith('{') && text.EndsWith('}'))
            return ParseInlineMap(text);

        if (text.StartsWith('"') || text.StartsWith('\''))
            return Unquote(text);

        // Quoted value like `"0x06070D"` — strip quotes, keep the hex as string.
        return ParseBare(text);
    }

    private static List<string> ParseInlineArray(string text)
    {
        var inner = text[1..^1];
        var items = new List<string>();
        foreach (var part in inner.Split(','))
        {
            var t = part.Trim();
            if (t.Length == 0) continue;
            items.Add(Unquote(t));
        }
        return items;
    }

    private static Dictionary<string, object?> ParseInlineMap(string text)
    {
        var inner = text[1..^1];
        var map = new Dictionary<string, object?>();
        // Split top-level commas (not inside quotes).
        foreach (var part in SplitTopLevel(inner, ','))
        {
            var colon = FindKeyColon(part);
            if (colon < 0) continue;
            var key = Unquote(part[..colon].Trim());
            var value = ParseScalar(part[(colon + 1)..].Trim());
            map[key] = value;
        }
        return map;
    }

    private static object ParseBare(string text)
    {
        if (text == "true" || text == "false")
            return bool.Parse(text);
        if (text == "null" || text == "~")
            return null!;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return d;
        return text;
    }

    private static string Unquote(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 && ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\'')))
            return s[1..^1];
        return s;
    }

    private static int FindKeyColon(string line)
    {
        bool inSingle = false, inDouble = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '\'' && !inDouble) inSingle = !inSingle;
            else if (c == '"' && !inSingle) inDouble = !inDouble;
            else if (c == ':' && !inSingle && !inDouble) return i;
        }
        return -1;
    }

    private static IEnumerable<string> SplitTopLevel(string s, char sep)
    {
        var sb = new System.Text.StringBuilder();
        bool inSingle = false, inDouble = false;
        foreach (var c in s)
        {
            if (c == '\'' && !inDouble) inSingle = !inSingle;
            else if (c == '"' && !inSingle) inDouble = !inDouble;
            if (c == sep && !inSingle && !inDouble)
            {
                yield return sb.ToString();
                sb.Clear();
            }
            else sb.Append(c);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    private static string StripComment(string line)
    {
        bool inSingle = false, inDouble = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '\'' && !inDouble) inSingle = !inSingle;
            else if (c == '"' && !inSingle) inDouble = !inDouble;
            else if (c == '#' && !inSingle && !inDouble)
                return line[..i];
        }
        return line;
    }

    private static int CountIndent(string line)
    {
        int n = 0;
        while (n < line.Length && line[n] == ' ') n++;
        return n;
    }
}
