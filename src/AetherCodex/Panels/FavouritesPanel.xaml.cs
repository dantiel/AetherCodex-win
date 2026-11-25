using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AetherCodex.Models;

namespace AetherCodex.Panels;

public partial class FavouritesPanel : UserControl
{
    /// <summary>A titled section (Favourites / ÆtherLink) rendered by the panel.</summary>
    public sealed class Group
    {
        public string Header { get; set; } = "";
        public List<Favourite> Items { get; set; } = new();
    }

    /// <summary>Raised when the user opens a project (single click on a row).</summary>
    public event Action<Favourite>? OpenRequested;

    /// <summary>Raised when an ÆtherLink peer is starred into the global favourites.</summary>
    public event Action<Favourite>? AddRequested;

    /// <summary>Raised when a favourite is un-starred.</summary>
    public event Action<Favourite>? RemoveRequested;

    /// <summary>Raised from the refresh button to re-scan ÆtherLink.</summary>
    public event Action? RefreshRequested;

    public FavouritesPanel()
    {
        InitializeComponent();
    }

    /// <summary>Populate the two sections. ÆtherLink peers already present in the
    /// favourites list are expected to have been deduplicated by the caller.</summary>
    public void SetProjects(IEnumerable<Favourite> favourites, IEnumerable<Favourite> aetherlink)
    {
        var groups = new List<Group>();
        var fav = favourites.ToList();
        var link = aetherlink.ToList();

        if (fav.Count > 0) groups.Add(new Group { Header = "Favourites", Items = fav });
        if (link.Count > 0) groups.Add(new Group { Header = "ÆtherLink", Items = link });

        Groups.ItemsSource = groups;
        EmptyLabel.Visibility = groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnItemOpen(object sender, MouseButtonEventArgs e)
    {
        // Bubbling handler: a click on the action Button is consumed by that
        // Button's own Click, so this only fires for clicks on the row itself.
        if ((sender as FrameworkElement)?.DataContext is Favourite f)
            OpenRequested?.Invoke(f);
    }

    private void OnItemAction(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Favourite f)
        {
            if (f.Source == "aetherlink") AddRequested?.Invoke(f);
            else RemoveRequested?.Invoke(f);
        }
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke();
}
