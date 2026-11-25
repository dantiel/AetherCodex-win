using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace AetherCodex.Panels;

/// <summary>
/// A hover-revealed "copy" button placed next to chat messages. It stays
/// collapsed until the mouse is over <paramref name="hoverOwner"/> — IsMouseOver
/// is true over the owner or any descendant — then copies
/// <paramref name="copyText"/> on click and briefly flashes a checkmark.
/// </summary>
internal static class CopyButton
{
    public static Button Create(string copyText, FrameworkElement hoverOwner, Brush foreground)
    {
        var btn = new Button
        {
            Content = "📋",
            ToolTip = "Copy",
            FontSize = 12,
            Padding = new Thickness(6, 2, 6, 2),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = foreground,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Top,
        };

        // Hidden by default, revealed by a DataTrigger when the owning message is
        // hovered. A style setter (not a local value) keeps the trigger able to
        // override it — local values outrank style triggers in WPF precedence.
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Button.VisibilityProperty, Visibility.Collapsed));
        var show = new DataTrigger
        {
            Binding = new Binding(nameof(UIElement.IsMouseOver)) { Source = hoverOwner },
            Value = true,
        };
        show.Setters.Add(new Setter(Button.VisibilityProperty, Visibility.Visible));
        style.Triggers.Add(show);
        btn.Style = style;

        btn.Click += (_, _) =>
        {
            try { Clipboard.SetText(copyText); }
            catch (Exception) { /* clipboard can be momentarily locked by another app */ }

            btn.Content = "✓";
            _ = Task.Run(async () =>
            {
                await Task.Delay(900);
                btn.Dispatcher.Invoke(() => btn.Content = "📋");
            });
        };

        return btn;
    }
}
