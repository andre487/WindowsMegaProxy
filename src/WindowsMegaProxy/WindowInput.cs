using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WindowsMegaProxy;

public static class WindowInput
{
    private static bool initialized;
    public static void Initialize()
    {
        if (initialized) return;
        initialized = true;
        EventManager.RegisterClassHandler(typeof(ScrollViewer), Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler(MouseWheel));
        var style = new Style(typeof(ComboBox), Application.Current.TryFindResource(typeof(ComboBox)) as Style);
        style.Setters.Add(new Setter(VirtualizingPanel.ScrollUnitProperty, ScrollUnit.Pixel));
        style.Setters.Add(new Setter(ScrollViewer.CanContentScrollProperty, true));
        style.Setters.Add(new Setter(ItemsControl.ItemsPanelProperty, new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)))));
        Application.Current.Resources[typeof(ComboBox)] = style;
    }
    public static double WheelDistance(int delta, int lines, double lineHeight, double viewport) =>
        -delta / 120.0 * (lines < 0 ? viewport : lines * lineHeight);

    public static void MouseWheel(object sender, MouseWheelEventArgs e)
    {
        var node = e.OriginalSource as DependencyObject;
        while (node is not null)
        {
            if (node is ScrollViewer viewer && viewer.ScrollableHeight > 0)
            {
                // WPF's pixel mode uses 48 DIP per line; use actual text lines and the Windows wheel setting.
                viewer.ScrollToVerticalOffset(viewer.VerticalOffset + WheelDistance(e.Delta, SystemParameters.WheelScrollLines,
                    viewer.FontSize * viewer.FontFamily.LineSpacing, viewer.ViewportHeight));
                e.Handled = true;
                return;
            }
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
    }
}
