using System.Collections.ObjectModel;
using System.ComponentModel;
using AetherCodex.Editor;

namespace AetherCodex.Models;

/// <summary>A node in the file browser. Directories are lazy: children load on expand.
/// The tree honours `.aether-properties` exclude/include rules (carried on the root and
/// inherited by every descendant) in addition to the shared default directory ignores.</summary>
public sealed class FileItem : INotifyPropertyChanged
{
    public string Name { get; }
    public string FullPath { get; }
    public bool IsDirectory { get; }

    /// <summary>Project exclusion rules inherited from the root (null for detached nodes).</summary>
    public ProjectIgnore? Ignore { get; }

    /// <summary>True when this node's path is excluded (by itself or an ancestor).</summary>
    private readonly bool _excluded;

    public ObservableCollection<FileItem> Children { get; } = new();

    public string Glyph => IsDirectory ? "📁" : GlyphFor(Name);

    private static string GlyphFor(string name)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext switch
        {
            ".rb" or ".rake" or ".gemspec" => "◆",
            ".cs" => "♯",
            ".swift" => "🕊",
            ".py" => "🐍",
            ".js" or ".ts" or ".jsx" or ".tsx" => "§",
            ".json" or ".yml" or ".yaml" => "≡",
            ".md" or ".markdown" => "✎",
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".svg" => "◈",
            ".html" or ".htm" or ".css" or ".scss" or ".sass" or ".haml" => "◇",
            _ => "·",
        };
    }

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            OnPropertyChanged(nameof(IsExpanded));
            if (value && IsDirectory) LoadChildren();
        }
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(nameof(IsSelected)); }
    }

    private bool _loaded;

    public FileItem(string fullPath, bool isDirectory, ProjectIgnore? ignore = null, bool excluded = false)
    {
        FullPath = fullPath;
        IsDirectory = isDirectory;
        Ignore = ignore;
        _excluded = excluded;
        Name = Path.GetFileName(fullPath);
        if (string.IsNullOrEmpty(Name)) Name = fullPath; // drive root
        if (IsDirectory) Children.Add(Placeholder);
    }

    private static readonly FileItem Placeholder = new("", false);

    /// <summary>Find an already-loaded direct child by its file/directory name.</summary>
    public FileItem? FindChild(string name) =>
        Children.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public void LoadChildren()
    {
        if (_loaded) return;
        _loaded = true;
        Children.Clear();
        var ignore = Ignore;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(FullPath).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (IsHidden(dir)) continue;
                    var name = Path.GetFileName(dir);
                    if (ProjectIgnore.IsDefaultIgnored(name)) continue;
                    var rel = ignore?.RelativePath(dir) ?? name;
                    bool excluded = _excluded || (ignore?.MatchesExclude(rel, true) ?? false);
                    if (excluded && !(ignore?.MatchesInclude(rel, true) ?? false) && !(ignore?.HasIncludeInside(rel) ?? false)) continue;
                    Children.Add(new FileItem(dir, true, ignore, excluded));
                }
                catch { /* skip inaccessible */ }
            }
            foreach (var file in Directory.EnumerateFiles(FullPath).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                if (IsHidden(file)) continue;
                var name = Path.GetFileName(file);
                var rel = ignore?.RelativePath(file) ?? name;
                bool excluded = _excluded || (ignore?.MatchesExclude(rel, false) ?? false);
                if (excluded && !(ignore?.MatchesInclude(rel, false) ?? false)) continue;
                Children.Add(new FileItem(file, false, ignore, excluded));
            }
        }
        catch { /* skip inaccessible directory */ }
    }

    /// <summary>Re-read a directory's children (drops the loaded cache and reloads).</summary>
    public void Reload()
    {
        if (!IsDirectory) return;
        _loaded = false;
        LoadChildren();
    }

    private static bool IsHidden(string path)
    {
        try
        {
            var attr = File.GetAttributes(path);
            return attr.HasFlag(FileAttributes.Hidden) || attr.HasFlag(FileAttributes.System);
        }
        catch { return false; }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}