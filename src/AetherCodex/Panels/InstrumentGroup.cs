using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AetherCodex.Editor;

namespace AetherCodex.Panels;

/// <summary>
/// Groups the instrumenta of one turn into a single visual block. The group
/// header collapses the whole block to a one-line summary; each card keeps its
/// own expanded / compact / one-liner density independently.
/// </summary>
public sealed class InstrumentGroup : Border
{
    private readonly StackPanel _cards = new();
    private readonly TextBlock _headerText;
    private readonly TextBlock _chevron;
    private UiColors _ui;
    private bool _collapsed;

    public InstrumentGroup(SyntaxTheme theme, UiColors ui)
    {
        _ui = ui;
        _headerText = new TextBlock
        {
            Text = "⚒ Instrumente",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(ui.Complementary),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _chevron = new TextBlock
        {
            Text = "▾",
            FontSize = 10,
            Foreground = new SolidColorBrush(ui.MutedForeground),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var header = new DockPanel
        {
            Margin = new Thickness(4, 4, 4, 2),
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
        };
        DockPanel.SetDock(_chevron, Dock.Right);
        header.Children.Add(_chevron);
        header.Children.Add(_headerText);
        header.MouseLeftButtonUp += (_, _) => Toggle();

        var content = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        content.Children.Add(header);
        content.Children.Add(_cards);

        Child = content;
        Background = Brushes.Transparent;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        Margin = new Thickness(0, 6, 0, 6);
    }

    public int Count => _cards.Children.Count;

    public void Add(InstrumentumCard card)
    {
        _cards.Children.Add(card);
        _headerText.Text = $"⚒ Instrumente · {Count}";
    }

    public void Collapse() { _collapsed = true; Refresh(); }
    public void Expand() { _collapsed = false; Refresh(); }

    /// <summary>Re-skin the group header and every instrumentum card after a
    /// live theme switch.</summary>
    public void ReTheme(SyntaxTheme theme, UiColors ui)
    {
        _ui = ui;
        _headerText.Foreground = new SolidColorBrush(ui.Complementary);
        _chevron.Foreground = new SolidColorBrush(ui.MutedForeground);
        foreach (var child in _cards.Children)
            if (child is InstrumentumCard card) card.ReTheme(theme, ui);
    }

    private void Toggle() { _collapsed = !_collapsed; Refresh(); }

    private void Refresh()
    {
        _cards.Visibility = _collapsed ? Visibility.Collapsed : Visibility.Visible;
        _chevron.Text = _collapsed ? "▸" : "▾";
    }
}