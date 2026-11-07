using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace WindowsMegaProxy;

public static class WindowInput
{
    private static bool initialized;
    public static void Initialize()
    {
        if (initialized) return;
        initialized = true;
        EventManager.RegisterClassHandler(typeof(ScrollViewer), Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler(MouseWheel));
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(WindowLoaded));
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
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && ScrollHorizontal(node, -e.Delta)) { e.Handled = true; return; }
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

    private static void WindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Window window || e.OriginalSource != window || PresentationSource.FromVisual(window) is not HwndSource source) return;
        HwndSourceHook hook = (IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (message != 0x020E) return IntPtr.Zero; // WPF does not route WM_MOUSEHWHEEL from precision touchpads.
            int delta = unchecked((short)(wParam.ToInt64() >> 16));
            long coordinates = lParam.ToInt64();
            var point = window.PointFromScreen(new Point(unchecked((short)coordinates), unchecked((short)(coordinates >> 16))));
            handled = ScrollHorizontal(window.InputHitTest(point) as DependencyObject, delta);
            return IntPtr.Zero;
        };
        source.AddHook(hook);
        window.Closed += (_, _) => { if (!source.IsDisposed) source.RemoveHook(hook); };
    }

    private static bool ScrollHorizontal(DependencyObject? node, int delta)
    {
        while (node is not null)
        {
            if (node is ScrollViewer viewer && viewer.ScrollableWidth > 0)
            {
                int characters = SystemParametersInfo(0x006C, 0, out uint value, 0) ? unchecked((int)value) : 3;
                var text = new FormattedText("0", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface(viewer.FontFamily, viewer.FontStyle, viewer.FontWeight, viewer.FontStretch), viewer.FontSize,
                    Brushes.Transparent, VisualTreeHelper.GetDpi(viewer).PixelsPerDip);
                viewer.ScrollToHorizontalOffset(viewer.HorizontalOffset - WheelDistance(delta, characters, text.Width, viewer.ViewportWidth));
                return true;
            }
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, out uint value, uint flags);
}
