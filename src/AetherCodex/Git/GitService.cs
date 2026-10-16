using System.Diagnostics;
using System.Text;

namespace AetherCodex.Git;

public enum GitChangeKind { Modified, Added, Deleted, Renamed, Untracked }

/// <summary>A single working-tree/index change, paths relative to the repo root.</summary>
public sealed record GitChange(
    GitChangeKind Kind,
    string Path,
    string OldPath,
    bool Staged,
    bool Unstaged)
{
    public string StatusLabel => Kind switch
    {
        GitChangeKind.Modified => "M",
        GitChangeKind.Added => "A",
        GitChangeKind.Deleted => "D",
        GitChangeKind.Renamed => "R",
        _ => "U",
    };
}

/// <summary>Thin process wrapper over the `git` CLI — the source of truth, never re-implemented.</summary>
public static class GitService
{
    /// <summary>Walk up from <paramref name="start"/> to the nearest `.git` (dir or file).</summary>
    public static string FindRoot(string start)
    {
        try
        {
            var dir = new DirectoryInfo(start);
            if (!dir.Exists) dir = new DirectoryInfo(Path.GetDirectoryName(start) ?? start);
            while (dir is not null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, ".git")) ||
                    File.Exists(Path.Combine(dir.FullName, ".git")))
                    return dir.FullName;
                dir = dir.Parent;
            }
        }
        catch { /* not a repo */ }
        return "";
    }

    public static bool IsRepository(string dir) => FindRoot(dir).Length > 0;

    public static IReadOnlyList<GitChange> GetStatus(string repoRoot)
    {
        var result = new List<GitChange>();
        var output = Run(repoRoot, "-c", "core.quotePath=false", "status",
            "--porcelain=v1", "--untracked-files=all");
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length < 4) continue;

            char x = line[0], y = line[1];
            string rest = line[3..].TrimEnd();

            if (x is 'R' or 'C')
            {
                var sep = rest.IndexOf(" -> ", StringComparison.Ordinal);
                var oldPath = Clean(sep >= 0 ? rest[..sep] : rest);
                var newPath = Clean(sep >= 0 ? rest[(sep + 4)..] : rest);
                result.Add(new GitChange(GitChangeKind.Renamed, newPath, oldPath,
                    Staged: x != ' ', Unstaged: y != ' '));
                continue;
            }

            var path = Clean(rest);
            var kind = (x == '?' && y == '?')
                ? GitChangeKind.Untracked
                : MapKind(y is not ' ' and not '?' ? y : x);
            result.Add(new GitChange(kind, path, "",
                Staged: x != ' ' && x != '?', Unstaged: y != ' '));
        }
        return result;
    }

    /// <summary>Unified diff for a change (combined staged+unstaged vs HEAD; synthetic for untracked).</summary>
    public static string GetDiff(string repoRoot, GitChange change)
    {
        if (change.Kind == GitChangeKind.Untracked)
            return SyntheticUntrackedDiff(repoRoot, change.Path);

        var paths = change.OldPath.Length > 0
            ? new[] { change.Path, change.OldPath }
            : new[] { change.Path };

        var args = new List<string> { "diff", "HEAD", "--" };
        args.AddRange(paths);
        var diff = Run(repoRoot, args.ToArray());

        if (string.IsNullOrWhiteSpace(diff))
        {
            var fallback = new List<string> { "diff", "--" };
            fallback.AddRange(paths);
            diff = Run(repoRoot, fallback.ToArray());
        }
        if (string.IsNullOrWhiteSpace(diff))
        {
            var staged = new List<string> { "diff", "--cached", "--" };
            staged.AddRange(paths);
            diff = Run(repoRoot, staged.ToArray());
        }

        return string.IsNullOrWhiteSpace(diff)
            ? $"# No diff available for {change.Path}"
            : diff;
    }

    public static void Stage(string repoRoot, string path) => Run(repoRoot, "add", "--", path);

    public static void Unstage(string repoRoot, string path) => Run(repoRoot, "restore", "--staged", "--", path);

    public static void Discard(string repoRoot, string path) => Run(repoRoot, "restore", "--", path);

    private static GitChangeKind MapKind(char status) => status switch
    {
        'A' => GitChangeKind.Added,
        'D' => GitChangeKind.Deleted,
        'R' or 'C' => GitChangeKind.Renamed,
        _ => GitChangeKind.Modified,
    };

    private static string Clean(string path)
    {
        var p = path.Trim();
        if (p.Length >= 2 && p[0] == '"' && p[^1] == '"')
            p = p[1..^1];
        return p.Replace("\\\"", "\"");
    }

    private static string SyntheticUntrackedDiff(string repoRoot, string path)
    {
        var full = Path.Combine(repoRoot, path);
        string content = "";
        if (File.Exists(full))
            content = File.ReadAllText(full);

        var norm = content.Replace("\r\n", "\n");
        var lines = norm.Split('\n');
        if (norm.EndsWith('\n')) lines = lines[..^1];

        var sb = new StringBuilder();
        sb.AppendLine($"diff --git a/{path} b/{path}");
        sb.AppendLine("new file mode 100644");
        sb.AppendLine("index 0000000..0000000");
        sb.AppendLine("--- /dev/null");
        sb.AppendLine($"+++ b/{path}");
        sb.AppendLine(lines.Length == 0 ? "@@ -0,0 +0,0 @@" : $"@@ -0,0 +1,{lines.Length} @@");
        foreach (var l in lines) sb.Append('+').AppendLine(l);
        return sb.ToString();
    }

    private static string Run(string workDir, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi);
            if (proc is null) return "";
            var stdout = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);
            return stdout;
        }
        catch { return ""; }
    }
}
