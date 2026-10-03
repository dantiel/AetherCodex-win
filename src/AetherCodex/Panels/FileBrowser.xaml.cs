using System.Collections.ObjectModel;
using System.Windows.Controls;
using AetherCodex.Models;

namespace AetherCodex.Panels;

public partial class FileBrowser : UserControl
{
    public event Action<string>? FileOpened;

    public FileBrowser()
    {
        InitializeComponent();
        Tree.SelectedItemChanged += (_, args) =>
        {
            if (args.NewValue is FileItem { IsDirectory: false } file)
                FileOpened?.Invoke(file.FullPath);
        };
    }

    public void SetRoot(string directory)
    {
        if (!Directory.Exists(directory)) return;
        var root = new FileItem(directory, true);
        root.LoadChildren();
        Tree.ItemsSource = new ObservableCollection<FileItem> { root };
        root.IsExpanded = true;
    }
}
