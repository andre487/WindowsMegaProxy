using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Data;
using System.Windows.Automation;

namespace WindowsMegaProxy;

public sealed class PathTextBox : TextBox
{
    public static readonly DependencyProperty FullPathProperty = DependencyProperty.Register(nameof(FullPath), typeof(string),
        typeof(PathTextBox), new PropertyMetadata("", (owner, _) => ((PathTextBox)owner).Refresh()));
    public string FullPath { get => (string)GetValue(FullPathProperty); set => SetValue(FullPathProperty, value); }

    public PathTextBox()
    {
        IsReadOnly = true;
        BorderThickness = new Thickness(0);
        Background = Brushes.Transparent;
        Padding = new Thickness(4, 2, 4, 2);
        FontWeight = FontWeights.Normal;
        SetBinding(ForegroundProperty, new Binding("Foreground") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(DataGridCell), 1) });
        SetBinding(CaretBrushProperty, new Binding("Foreground") { Source = this });
        VerticalContentAlignment = VerticalAlignment.Center;
        SizeChanged += (_, _) => Refresh();
        GotKeyboardFocus += (_, _) => { Text = FullPath; SelectAll(); };
        LostKeyboardFocus += (_, _) => Refresh();
    }

    public static string Shorten(string path, Func<string, bool> fits)
    {
        if (fits(path)) return path;
        int separator = path.LastIndexOfAny(new[] { '\\', '/' });
        if (separator < 0) return path;
        string suffix = separator >= 0 ? path[separator..] : path;
        int prefix = Math.Min(separator >= 0 ? separator : path.Length, 32);
        while (prefix > 0 && !fits(path[..prefix] + "…" + suffix)) prefix--;
        return path[..prefix] + "…" + suffix;
    }

    private void Refresh()
    {
        ToolTip = FullPath;
        AutomationProperties.SetName(this, FullPath);
        if (IsKeyboardFocusWithin) { Text = FullPath; return; }
        Text = Shorten(FullPath, text => new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection,
            new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize, Foreground,
            VisualTreeHelper.GetDpi(this).PixelsPerDip).WidthIncludingTrailingWhitespace <= Math.Max(0, ActualWidth - 10));
    }
}
