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

    /// <summary>Context markers consulted for file filtering, in precedence order.
    /// `.aethercodex` (YAML) is authoritative; the flat properties files are the
    /// lightweight alternative.</summary>
    public static readonly string[] FilterFileNames = { ".aethercodex", ".aether-properties", ".aether_properties" };

    /// <summary>Keys that are project configuration (exclude/include rules and daemon
    /// config), never extension→language mappings.</summary>
    private static readonly HashSet<string> ReservedKeys = new(StringComparer.OrdinalIgnoreCase)
    { "exclude", "include", "model", "fast-model", "api-type", "api-url", "api-key",
      "port", "dev_mode", "dev-mode", "reasoning-model", "name", "contexts",
      "includeDirectories", "excludeDirectories", "includeFiles", "excludeFiles",
      "includeInBrowser", "excludeInBrowser", "includeDirectoriesInBrowser", "excludeDirectoriesInBrowser",
      "includeFilesInBrowser", "excludeFilesInBrowser",
      "includeInFileChooser", "excludeInFileChooser", "includeFilesInFileChooser",
      "excludeDirectoriesInFileChooser", "excludeFilesInFileChooser",
      "excludeInFolderSearch", "excludeDirectoriesInFolderSearch", "excludeFilesInFolderSearch",
      "includeInArgonaut", "excludeInArgonaut" };

    /// <summary>Extension (`.foo`) → language key (`haml`). Keys are normalised to a
    /// leading dot so `*.umghaml`, `.umghaml` and `umghaml` all resolve identically.</summary>
    public static Dictionary<string, string> LoadSyntaxOverrides(string? startPath)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var file = LocateFile(startPath);
        if (file is null) return result;

        foreach (var line in File.ReadAllLines(file))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] is '#' or '!' or ';') continue;

            int eq = trimmed.IndexOfAny(new[] { '=', ':' });
            if (eq <= 0) continue;

            var rawKey = trimmed[..eq].Trim();
            if (ReservedKeys.Contains(rawKey)) continue;

            var key = NormalizeKey(rawKey);
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

    public static string? LocateFile(string? startPath)
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

    /// <summary>Locate the nearest context marker carrying file-filtering rules —
    /// `.aethercodex` (YAML) first, then the flat `.aether-properties` /
    /// `.aether_properties` — walking up from <paramref name="startPath"/>.</summary>
    public static string? LocateFilterFile(string? startPath)
    {
        if (string.IsNullOrEmpty(startPath)) return null;
        var dir = Directory.Exists(startPath) ? startPath : Path.GetDirectoryName(startPath);
        while (!string.IsNullOrEmpty(dir))
        {
            foreach (var name in FilterFileNames)
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