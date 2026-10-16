using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AetherCodex.Models;

namespace AetherCodex.Panels;

public partial class FileBrowser : UserControl
{
    public event Action<string>? FileOpened;
    public event Action<string>? RevealInExplorer;
    public event Action<string>? RenameRequested;
    public event Action<string>? DeleteRequested;
    public event Action<string>? DuplicateRequested;
    public event Action<string, bool>? NewItemRequested;

    private FileItem? _root;
    private bool _suppressSelection;

    public FileBrowser()
    {
        InitializeComponent();
        Tree.SelectedItemChanged += (_, args) =>
        {
            if (_suppressSelection) return;
            if (args.NewValue is FileItem { IsDirectory: false } file)
                FileOpened?.Invoke(file.FullPath);
        };
        Tree.PreviewMouseRightButtonDown += OnTreePreviewMouseRightButtonDown;
        Tree.ContextMenuOpening += OnTreeContextMenuOpening;
    }

    public void SetRoot(string directory)
    {
        if (!Directory.Exists(directory)) return;
        _root = new FileItem(directory, true);
        _root.LoadChildren();
        Tree.ItemsSource = new ObservableCollection<FileItem> { _root };
        _root.IsExpanded = true;
    }

    /// <summary>Expand the tree down to <paramref name="path"/> and scroll the file
    /// into view. Lazy directories load on the way down; a no-op when the file is
    /// outside the current root.</summary>
    public void Reveal(string path)
    {
        if (_root is null || string.IsNullOrEmpty(path) || !File.Exists(path)) return;

        var rel = GetRelativePath(_root.FullPath, path);
        var parts = rel.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                              StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;

        var current = _root;
        for (int i = 0; i < parts.Length; i++)
        {
            current.LoadChildren();
            var next = current.FindChild(parts[i]);
            if (next is null) return;

            if (i < parts.Length - 1)
            {
                current.IsExpanded = true; // triggers lazy load of the next level
                current = next;
            }
            else
            {
                current = next;
            }
        }

        _suppressSelection = true;
        try { current.IsSelected = true; }
        finally { _suppressSelection = false; }

        // Container generation is deferred until after the layout pass. For a deeply
        // nested node, `ContainerFromItem` on the TreeView only knows top-level items,
        // so walk the realised container tree to find the leaf and scroll it into view.
        var captured = current;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            Tree.UpdateLayout();
            if (FindContainer(Tree.ItemContainerGenerator, captured) is { } tvi)
                tvi.BringIntoView();
        }));
    }

    /// <summary>Recursively locate the realised <see cref="TreeViewItem"/> for a data item.</summary>
    private static TreeViewItem? FindContainer(ItemContainerGenerator generator, object target)
    {
        for (int i = 0; i < generator.Items.Count; i++)
        {
            if (generator.ContainerFromIndex(i) is not TreeViewItem tvi) continue;
            if (ReferenceEquals(tvi.DataContext, target)) return tvi;
            if (FindContainer(tvi.ItemContainerGenerator, target) is { } nested) return nested;
        }
        return null;
    }

    /// <summary>Reload a (loaded) directory node by path so its children reflect the disk.</summary>
    public void Refresh(string directory)
    {
        if (FindLoadedNode(_root, directory) is { IsDirectory: true } node)
            node.Reload();
    }

    private static string GetRelativePath(string root, string path)
    {
        try { return Path.GetRelativePath(root, path); }
        catch { return Path.GetFileName(path); }
    }

    private void OnTreePreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = FindTreeViewItem(e.OriginalSource as DependencyObject);
        if (item?.DataContext is not FileItem fi) return;

        _suppressSelection = true;
        try { fi.IsSelected = true; }
        finally { _suppressSelection = false; }
        Tree.Focus();
    }

    private void OnTreeContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (Tree.SelectedItem is not FileItem item)
        {
            e.Handled = true;
            return;
        }
        Tree.ContextMenu = BuildMenu(item);
    }

    private ContextMenu BuildMenu(FileItem item)
    {
        var menu = new ContextMenu();

        MenuItem Add(string header, Action onClick, string? gesture = null)
        {
            var mi = new MenuItem { Header = header, InputGestureText = gesture };
            mi.Click += (_, _) => onClick();
            menu.Items.Add(mi);
            return mi;
        }

        bool isRoot = ReferenceEquals(item, _root);

        if (!item.IsDirectory)
            Add("Open", () => FileOpened?.Invoke(item.FullPath), "Enter");
        else
        {
            Add("New File…", () => NewItemRequested?.Invoke(item.FullPath, false));
            Add("New Folder…", () => NewItemRequested?.Invoke(item.FullPath, true));
            Add("Refresh", () => { item.Reload(); item.IsExpanded = true; }, "F5");
        }

        if (!item.IsDirectory)
            Add("Duplicate", () => DuplicateRequested?.Invoke(item.FullPath));

        menu.Items.Add(new Separator());
        Add("Open in Explorer", () => RevealInExplorer?.Invoke(item.FullPath));
        Add("Copy Path", () => Clipboard.SetText(item.FullPath));
        if (!isRoot)
            Add("Copy Relative Path", () => Clipboard.SetText(RelativePath(item)));

        if (!isRoot)
        {
            menu.Items.Add(new Separator());
            Add("Rename…", () => RenameRequested?.Invoke(item.FullPath), "F2");
            Add("Delete", () => DeleteRequested?.Invoke(item.FullPath), "Del");
        }

        return menu;
    }

    private string RelativePath(FileItem item)
    {
        if (_root is null) return item.FullPath;
        try { return Path.GetRelativePath(_root.FullPath, item.FullPath); }
        catch { return item.Name; }
    }

    private static TreeViewItem? FindTreeViewItem(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is TreeViewItem tvi) return tvi;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    private static FileItem? FindLoadedNode(FileItem? node, string path)
    {
        if (node is null) return null;
        if (string.Equals(node.FullPath, path, StringComparison.OrdinalIgnoreCase)) return node;
        foreach (var child in node.Children)
        {
            if (child.FullPath.Length == 0) continue; // placeholder for a not-yet-loaded directory
            if (FindLoadedNode(child, path) is { } found) return found;
        }
        return null;
    }
}