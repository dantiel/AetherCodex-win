using System.Text;

namespace AetherCodex.Editor;

/// <summary>
/// Project-level `.aether-properties`: a flat properties file at the project root
/// carrying per-project editor overrides. The canonical use is mapping custom file
/// extensions to syntax languages, e.g.
/// <code>
/// # .aether-properties
/// .umghaml = haml
/// .umgss   = sass
/// </code>
/// Resolution walks up from the opened folder/file until it finds the file (or hits
/// a filesystem root). `.aether_properties` (underscore, the daemon's spelling) is
/// recognised as a fallback so existing contexts keep working.
/// </summary>
public static class ProjectProperties
{
    public static readonly string[] FileNames = { ".aether-properties", ".aether_properties" };

    /// <summary>Extension (`.foo`) → language key (`haml`). Keys are normalised to a
    /// leading dot so `*.umghaml`, `.umghaml` and `umghaml` all resolve identically.</summary>
    public static Dictionary<string, string> LoadSyntaxOverrides(string? startPath)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var file = Locate(startPath);
        if (file is null) return result;

        foreach (var line in File.ReadAllLines(file))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] is '#' or '!' or ';') continue;

            int eq = trimmed.IndexOfAny(new[] { '=', ':' });
            if (eq <= 0) continue;

            var key = NormalizeKey(trimmed[..eq]);
            var value = trimmed[(eq + 1)..].Trim().Trim('\'', '"');
            if (key.Length == 0 || value.Length == 0) continue;
            result[key] = value;
        }
        return result;
    }

    private static string NormalizeKey(string raw)
    {
        var key = raw.Trim();
        if (key.StartsWith("*.")) key = key[1..];
        if (key.Length > 0 && key[0] != '.') key = "." + key;
        return key;
    }

    private static string? Locate(string? startPath)
    {
        if (string.IsNullOrEmpty(startPath)) return null;
        var dir = Directory.Exists(startPath) ? startPath : Path.GetDirectoryName(startPath);
        while (!string.IsNullOrEmpty(dir))
        {
            foreach (var name in FileNames)
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate)) return candidate;
            }
            var parent = Path.GetDirectoryName(dir);
            if (parent == dir) break; // filesystem root
            dir = parent;
        }
        return null;
    }
}
