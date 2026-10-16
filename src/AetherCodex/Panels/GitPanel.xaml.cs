using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AetherCodex.Git;

namespace AetherCodex.Panels;

public partial class GitPanel : UserControl
{
    public event Action<string>? DiffRequested;
    public event Action<string>? FileOpenRequested;

    private string _repoRoot = "";
    private readonly List<GitChange> _changes = new();

    public GitPanel()
    {
        InitializeComponent();
        RenderEmpty("No repository open");
    }

    public void SetRepository(string directory)
    {
        _repoRoot = GitService.FindRoot(directory);
        Refresh();
    }

    public void Refresh()
    {
        _changes.Clear();
        List.Items.Clear();

        if (_repoRoot.Length == 0 || !Directory.Exists(_repoRoot))
        {
            RenderEmpty("No repository open");
            return;
        }

        try
        {
            _changes.AddRange(GitService.GetStatus(_repoRoot));
        }
        catch { _changes.Clear(); }

        if (_changes.Count == 0)
        {
            RenderEmpty("Working tree clean");
            return;
        }

        int staged = _changes.Count(c => c.Staged);
        int unstaged = _changes.Count(c => c.Unstaged);
        SummaryText.Text = $"{_changes.Count} change{(_changes.Count == 1 ? "" : "s")}" +
                           (staged > 0 && unstaged > 0 ? " · staged+unstaged" : "");

        foreach (var change in _changes.OrderBy(c => c.Path, StringComparer.OrdinalIgnoreCase))
            List.Items.Add(BuildItem(change));
    }

    private void RenderEmpty(string message)
    {
        SummaryText.Text = "";
        var item = new ListBoxItem
        {
            IsHitTestVisible = false,
            Content = new TextBlock
            {
                Text = message,
                Foreground = (Brush)FindResource("MutedForeground"),
                FontSize = 11,
                Margin = new Thickness(12, 6, 0, 0),
            },
        };
        List.Items.Add(item);
    }

    private ListBoxItem BuildItem(GitChange change)
    {
        var badge = new TextBlock
        {
            Text = change.StatusLabel,
            FontFamily = (FontFamily)FindResource("MonoFont"),
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Width = 16,
            Foreground = StatusBrush(change),
        };

        var label = change.OldPath.Length > 0
            ? $"{change.Path}  ←  {change.OldPath}"
            : change.Path;
        var path = new TextBlock
        {
            Text = label,
            Foreground = (Brush)FindResource("SidebarForeground"),
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 2, 4, 2) };
        panel.Children.Add(badge);
        panel.Children.Add(path);

        var item = new ListBoxItem
        {
            Content = panel,
            Tag = change,
            ToolTip = change.Path,
        };
        item.ContextMenu = BuildMenu(change);
        return item;
    }

    private ContextMenu BuildMenu(GitChange change)
    {
        var menu = new ContextMenu();

        MenuItem Add(string header, Action onClick)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) => onClick();
            menu.Items.Add(mi);
            return mi;
        }

        Add("Open Diff", () => DiffRequested?.Invoke(change.Path));
        if (change.Kind != GitChangeKind.Deleted)
            Add("Open File", () => FileOpenRequested?.Invoke(Path.Combine(_repoRoot, change.Path)));

        menu.Items.Add(new Separator());

        if (change.Staged)
            Add("Unstage", () => Mutate(() => GitService.Unstage(_repoRoot, change.Path)));
        else if (change.Kind != GitChangeKind.Untracked)
            Add("Stage", () => Mutate(() => GitService.Stage(_repoRoot, change.Path)));

        if (change.Kind != GitChangeKind.Untracked && change.Unstaged)
        {
            Add("Discard Changes…", () =>
            {
                if (MessageBox.Show($"Discard changes to '{change.Path}'?\nThis cannot be undone.",
                        "Discard", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                    Mutate(() => GitService.Discard(_repoRoot, change.Path));
            });
        }

        return menu;
    }

    private void Mutate(Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Git", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        Refresh();
    }

    private void OpenSelected()
    {
        if (List.SelectedItem is ListBoxItem { Tag: GitChange change })
            DiffRequested?.Invoke(change.Path);
    }

    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelected();

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { OpenSelected(); e.Handled = true; }
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => Refresh();

    private static Brush StatusBrush(GitChange change)
    {
        var c = change.Kind switch
        {
            GitChangeKind.Modified => Color.FromRgb(0xD1, 0x9A, 0x66),
            GitChangeKind.Added => Color.FromRgb(0x3F, 0xB9, 0x50),
            GitChangeKind.Deleted => Color.FromRgb(0xF8, 0x51, 0x49),
            GitChangeKind.Renamed => Color.FromRgb(0x58, 0xA6, 0xFF),
            _ => Color.FromRgb(0x8B, 0x94, 0x9E),
        };
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        return brush;
    }
}
