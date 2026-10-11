using System.Collections.ObjectModel;
using System.ComponentModel;

namespace AetherCodex.Models;

/// <summary>A node in the file browser. Directories are lazy: children load on expand.</summary>
public sealed class FileItem : INotifyPropertyChanged
{
    public string Name { get; }
    public string FullPath { get; }
    public bool IsDirectory { get; }

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
            ".html" or ".htm" or ".css" or ".scss" => "◇",
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

    public FileItem(string fullPath, bool isDirectory)
    {
        FullPath = fullPath;
        IsDirectory = isDirectory;
        Name = Path.GetFileName(fullPath);
        if (string.IsNullOrEmpty(Name)) Name = fullPath; // drive root
        if (IsDirectory) Children.Add(Placeholder);
    }

    private static readonly FileItem Placeholder = new("", false);

    public void LoadChildren()
    {
        if (_loaded) return;
        _loaded = true;
        Children.Clear();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(FullPath).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                try { if (!IsHidden(dir)) Children.Add(new FileItem(dir, true)); }
                catch { /* skip inaccessible */ }
            }
            foreach (var file in Directory.EnumerateFiles(FullPath).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                if (IsHidden(file)) continue;
                Children.Add(new FileItem(file, false));
            }
        }
        catch { /* skip inaccessible directory */ }
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