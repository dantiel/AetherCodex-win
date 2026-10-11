using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AetherCodex.Panels;

/// <summary>
/// C# mirror of Ruby's <c>oracle/instrumentum_renderer.rb</c> — the single source
/// of truth for how an instrumentum (tool) result is summarised and laid out.
/// read_file / run_command / patch_file / … always render as a tool-aware panel
/// (glyph, coloured status digest, tool-aware body) instead of a raw JSON blob.
/// </summary>
public static class InstrumentumRenderer
{
    /// <summary>Safety cap: an expanded body never renders more than this many lines.</summary>
    public const int BodyCap = 400;

    private static readonly IReadOnlyDictionary<string, string> Glyphs =
        new Dictionary<string, string>
        {
            ["aegis"] = "ae",
            ["read_file"] = "->",
            ["run_command"] = "$",
            ["create_file"] = "+",
            ["rename_file"] = "->",
            ["patch_file"] = "~",
            ["symbolic_patch_file"] = "~",
            ["file_overview"] = "@",
            ["recall_notes"] = "?",
            ["recall_corpus"] = "?",
            ["corpus_sound"] = "@",
            ["remember"] = "*",
            ["remove_note"] = "-",
        };

    public static string Glyph(string name)
        => Glyphs.TryGetValue(name, out var g) ? g : "*";

    public static bool IsOk(JsonNode? result)
    {
        var o = Obj(Normalize(result));
        if (o is not null)
        {
            if (HasError(o)) return false;
            if (o["exit_status"] is JsonValue v && v.TryGetValue<long>(out var code))
                return code == 0;
        }
        return true;
    }

    public static string Summary(string name, JsonNode? args, JsonNode? result)
    {
        var o = Obj(Normalize(result));
        if (o is not null && HasError(o))
        {
            var err = Compact(o["error"]);
            return err.Length == 0 ? "fehlgeschlagen" : Truncate(err, 80);
        }

        var a = Obj(args);
        switch (name)
        {
            case "read_file":
            {
                var content = S(o?["content"]);
                var path = S(a?["path"]);
                var n = content.Length == 0 ? 0 : content.Count(c => c == '\n') + 1;
                return path.Length == 0 ? $"{n} Zeilen" : $"{path} - {n} Zeilen";
            }
            case "run_command":
            {
                var code = o?["exit_status"];
                if (code is JsonValue c && c.TryGetValue<long>(out var l)) return $"exit {l}";
                return "ausgeführt";
            }
            case "patch_file":
            case "symbolic_patch_file":
            {
                var path = S(a?["path"]);
                var edits = S(a?["diff"]).Split("<<<<<<< SEARCH").Length - 1;
                var base_ = $"{edits} Edit{(edits == 1 ? "" : "s")}";
                return path.Length == 0 ? base_ : $"{base_} - {path}";
            }
            case "remember":
            {
                var snippet = Compact(a?["content"]);
                return snippet.Length == 0 ? "gespeichert" : Truncate(snippet, 40);
            }
            case "aegis":
            {
                var notes = Arr(o?["aegis_notes"]);
                return $"{notes?.Count ?? 0} Notizen";
            }
            case "file_overview":
            {
                var fi = Obj(o?["file_info"]);
                var path = S(fi?["path"]);
                if (path.Length == 0) path = S(a?["path"]);
                if (path.Length == 0) return "überblickt";
                var lines = fi?["lines"];
                if (lines is JsonValue lv && lv.TryGetValue<long>(out var ln)) return $"{path} - {ln} Zeilen";
                return path;
            }
            case "rename_file":
            {
                var from = S(a?["from"]);
                var to = S(a?["to"]);
                return from.Length == 0 ? "umbenannt" : $"{from} → {to}";
            }
            case "create_file":
            {
                var path = S(a?["path"]);
                return path.Length == 0 ? "erstellt" : path;
            }
            case "remove_note":
                return S(a?["id"]);
            default:
            {
                var text = Compact(ResultText(result));
                return text.Length == 0 ? "erledigt" : Truncate(text, 40);
            }
        }
    }

    /// <summary>Compact digest of a tool call's arguments for the live start line.</summary>
    public static string ArgsSummary(string name, JsonNode? args)
    {
        var a = Obj(args);
        var digest = name switch
        {
            "read_file" => FileHeader(a),
            "run_command" => S(a?["cmd"]),
            "patch_file" or "symbolic_patch_file" => S(a?["path"]),
            "create_file" => S(a?["path"]),
            "rename_file" => $"{S(a?["from"])} -> {S(a?["to"])}",
            "file_overview" => S(a?["path"]),
            "recall_notes" or "recall_corpus" => S(a?["query"]),
            "remember" => Truncate(Compact(a?["content"]), 40),
            _ => "",
        };
        var summary = Truncate(Compact(digest), 72);
        return summary.Length == 0 ? "" : "  " + summary;
    }

    /// <summary>Tool-aware body lines. <paramref name="mono"/> tells the card whether
    /// the body is code (monospace) or prose.</summary>
    public static List<string> BuildBody(string name, JsonNode? args, JsonNode? result, out bool mono)
    {
        mono = true;
        var o = Obj(Normalize(result));
        if (o is not null && HasError(o))
        {
            mono = false;
            return new List<string> { "⚠ " + Compact(o["error"]) };
        }

        var lines = name switch
        {
            "read_file" => ReadFileBody(args, o, out mono),
            "run_command" => RunCommandBody(args, o, out mono),
            "remember" => RememberBody(args, o, out mono),
            "patch_file" or "symbolic_patch_file" => PatchFileBody(args, o, out mono),
            "aegis" => AegisBody(o, out mono),
            "file_overview" => FileOverviewBody(o, out mono),
            "rename_file" => RenameBody(args, out mono),
            "create_file" => CreateBody(args, out mono),
            _ => GenericBody(result, out mono),
        };
        return Cap(lines);
    }

    // ── Body builders ──────────────────────────────────────────────────────

    private static List<string> ReadFileBody(JsonNode? args, JsonObject? o, out bool mono)
    {
        mono = true;
        var lines = new List<string>();
        var header = FileHeader(Obj(args));
        if (header.Length > 0) { lines.Add(header); lines.Add(""); }
        var content = S(o?["content"]);
        lines.AddRange(content.Length == 0 ? new[] { "(leer)" } : SplitLines(content));
        return lines;
    }

    private static List<string> RunCommandBody(JsonNode? args, JsonObject? o, out bool mono)
    {
        mono = true;
        var lines = new List<string>();
        var cmd = S(Obj(args)?["cmd"]);
        if (cmd.Length > 0) lines.Add("$ " + cmd);

        var code = o?["exit_status"];
        var exitTxt = code is JsonValue c && c.TryGetValue<long>(out var l) ? $"exit {l}" : "ausgeführt";
        var cwd = S(o?["cwd"]);
        lines.Add(exitTxt + (cwd.Length > 0 ? $" - cwd {cwd}" : ""));

        var raw = S(o?["result"]);
        if (raw.Length == 0) raw = S(o?["content"]);
        if (raw.StartsWith("Command output:", StringComparison.Ordinal))
            raw = raw["Command output:".Length..].TrimStart();
        var outLines = SplitLines(raw);
        if (outLines.Count > 0) { lines.Add(""); lines.AddRange(outLines); }
        return lines;
    }

    private static List<string> RememberBody(JsonNode? args, JsonObject? o, out bool mono)
    {
        mono = false;
        var a = Obj(args);
        var lines = new List<string>();
        var id = S(a?["id"]);
        if (id.Length == 0) id = S(o?["id"]);
        lines.Add(id.Length > 0 ? $"Notiz #{id}" : "Notiz");
        var content = S(a?["content"]);
        if (content.Length > 0) lines.Add(content);
        var tags = StringList(a?["tags"]);
        if (tags.Count > 0) lines.Add("Tags   " + string.Join(", ", tags));
        var links = StringList(a?["links"]);
        if (links.Count > 0) lines.Add("Links  " + string.Join(", ", links));
        return lines;
    }

    private static List<string> PatchFileBody(JsonNode? args, JsonObject? o, out bool mono)
    {
        mono = true;
        var a = Obj(args);
        var lines = new List<string>();
        var path = S(a?["path"]);
        if (path.Length > 0) lines.Add("Pfad  " + path);
        var diff = S(a?["diff"]);
        var n = diff.Split("<<<<<<< SEARCH").Length - 1;
        if (n > 0) lines.Add($"{n} Edit{(n == 1 ? "" : "s")}");
        if (diff.Length > 0)
        {
            lines.Add("");
            lines.Add("-- diff --");
            lines.AddRange(SplitLines(diff));
        }
        return lines;
    }

    private static List<string> AegisBody(JsonObject? o, out bool mono)
    {
        mono = false;
        var lines = new List<string>();
        var orient = Obj(o?["aegis_orientation"]);
        var summary = S(orient?["summary"]);
        if (summary.Length > 0) lines.Add("Summary  " + summary);
        var meta = new List<string>();
        if (orient?["temperature"] is not null) meta.Add("temperature=" + S(orient["temperature"]));
        if (orient?["thinking"] is not null) meta.Add("thinking=" + S(orient["thinking"]));
        if (meta.Count > 0) lines.Add(string.Join(" - ", meta));

        var notes = Arr(o?["aegis_notes"]);
        if (notes is { Count: > 0 })
        {
            lines.Add("");
            lines.Add($"Notizen ({notes.Count})");
            foreach (var note in notes)
            {
                if (note is not JsonObject no) continue;
                var id = S(no["id"]);
                var content = Compact(no["content"]);
                lines.Add((id.Length > 0 ? $"#{id} " : "- ") + Truncate(content, 120));
            }
        }
        return lines;
    }

    private static List<string> FileOverviewBody(JsonObject? o, out bool mono)
    {
        mono = false;
        var lines = new List<string>();
        var fi = Obj(o?["file_info"]);
        var path = S(fi?["path"]);
        if (path.Length > 0)
        {
            lines.Add("@ " + path);
            var stats = new List<string>();
            if (fi?["lines"] is not null) stats.Add(S(fi["lines"]) + " Zeilen");
            if (fi?["size"] is not null) stats.Add(S(fi["size"]) + " Bytes");
            if (stats.Count > 0) lines.Add("  " + string.Join(" · ", stats));
        }
        Section(lines, "Struktur", StructuralSummary(o?["structural_summary"]));
        Section(lines, "Symbole", S(o?["symbolic_overview"]));
        Section(lines, "Tag-Resonanz", S(o?["tag_cloud"]));
        Section(lines, "Datei-Resonanz", S(o?["file_cloud"]));
        if (o?["hermetic_overview"] is JsonArray ha)
            Section(lines, "Hermetik", string.Join(", ", ha.Select(S)));
        else
            Section(lines, "Hermetik", S(o?["hermetic_overview"]));
        return lines;
    }

    private static List<string> RenameBody(JsonNode? args, out bool mono)
    {
        mono = false;
        var a = Obj(args);
        return new List<string> { $"{S(a?["from"])}  →  {S(a?["to"])}" };
    }

    private static List<string> CreateBody(JsonNode? args, out bool mono)
    {
        mono = false;
        var a = Obj(args);
        var lines = new List<string>();
        var path = S(a?["path"]);
        lines.Add(path.Length > 0 ? "+ " + path : "+ created");
        var content = S(a?["content"]);
        if (content.Length > 0) { lines.Add(""); lines.AddRange(SplitLines(content)); }
        return lines;
    }

    private static List<string> GenericBody(JsonNode? result, out bool mono)
    {
        mono = true;
        var text = ResultText(result);
        if (string.IsNullOrWhiteSpace(text)) return new List<string> { "(leer)" };
        return SplitLines(text);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static string StructuralSummary(JsonNode? node)
    {
        var o = Obj(node);
        if (o is null) return S(node);
        var parts = new List<string>();
        var lang = S(o["language"]);
        if (lang.Length > 0) parts.Add(lang);
        void Add(string key, string label)
        {
            var v = S(o[key]);
            if (v.Length > 0) parts.Add($"{v} {label}");
        }
        Add("containers", "containers");
        Add("members", "members");
        Add("imports", "imports");
        Add("exports", "exports");
        Add("total_symbols", "symbols");
        return string.Join(" · ", parts);
    }

    private static void Section(List<string> lines, string title, string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return;
        if (lines.Count > 0) lines.Add("");
        lines.Add("── " + title + " ──");
        lines.AddRange(SplitLines(body));
    }

    private static List<string> Cap(List<string> lines)
    {
        if (lines.Count <= BodyCap) return lines;
        var capped = lines.Take(BodyCap).ToList();
        capped.Add($"… {lines.Count - BodyCap} weitere Zeilen");
        return capped;
    }

    private static string ResultText(JsonNode? result)
    {
        var o = Obj(Normalize(result));
        if (o is not null)
        {
            var content = S(o["content"]);
            if (content.Length > 0) return content;
            var res = S(o["result"]);
            if (res.Length > 0) return res;
        }
        return S(result);
    }

    private static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        foreach (var l in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            lines.Add(l);
        return lines;
    }

    private static string FileHeader(JsonObject? a)
    {
        var path = S(a?["path"]);
        if (path.Length == 0) return "";
        if (a?["range"] is JsonArray { Count: 2 } range)
            return $"{path}[{S(range[0])}..{S(range[1])}]";
        return path;
    }

    private static bool HasError(JsonObject o) => S(o["error"]).Length > 0;

    /// <summary>The daemon's <c>ui_preview</c> serialises most hash results into a
    /// JSON string (read_file / file_overview stay structured). Parse those back
    /// so run_command / aegis / … can be inspected by key.</summary>
    private static JsonNode? Normalize(JsonNode? result)
    {
        if (result is JsonValue v && v.TryGetValue<string>(out var s))
        {
            var t = s.TrimStart();
            if (t.Length > 0 && (t[0] == '{' || t[0] == '['))
            {
                try { return JsonNode.Parse(s); }
                catch (System.Text.Json.JsonException) { return result; }
            }
        }
        return result;
    }

    private static JsonObject? Obj(JsonNode? node) => node as JsonObject;

    private static JsonArray? Arr(JsonNode? node) => node as JsonArray;

    private static List<string> StringList(JsonNode? node)
    {
        var list = new List<string>();
        if (node is JsonArray arr)
        {
            foreach (var item in arr) list.Add(S(item));
        }
        else if (node is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0)
        {
            list.Add(s);
        }
        return list;
    }

    private static string Compact(JsonNode? node)
        => Regex.Replace(S(node), @"\s+", " ").Trim();

    private static string S(JsonNode? node)
    {
        if (node is null) return "";
        if (node is JsonValue v)
        {
            if (v.TryGetValue<string>(out var s)) return s;
            return v.ToString(); // numbers/bools → "42" / "true"
        }
        return node.ToJsonString();
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}