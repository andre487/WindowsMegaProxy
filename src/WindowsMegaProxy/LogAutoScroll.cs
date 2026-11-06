using System;
using System.Windows;
using System.Windows.Controls;

namespace WindowsMegaProxy;

public static class LogAutoScroll
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached("Enabled", typeof(bool),
        typeof(LogAutoScroll), new PropertyMetadata(false, EnabledChanged));
    private static readonly DependencyProperty FollowingProperty = DependencyProperty.RegisterAttached("Following", typeof(bool),
        typeof(LogAutoScroll), new PropertyMetadata(true));

    public static bool GetEnabled(DependencyObject owner) => (bool)owner.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject owner, bool value) => owner.SetValue(EnabledProperty, value);
    public static bool IsFollowing(TextBox box) => (bool)box.GetValue(FollowingProperty);
    private static bool AtBottom(TextBox box) => box.VerticalOffset >= Math.Max(0, box.ExtentHeight - box.ViewportHeight) - 1;

    private static void EnabledChanged(DependencyObject owner, DependencyPropertyChangedEventArgs e)
    {
        if (owner is not TextBox box) return;
        if ((bool)e.NewValue) box.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(Scrolled));
        else box.RemoveHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(Scrolled));
    }

    private static void Scrolled(object sender, ScrollChangedEventArgs e)
    {
        var box = (TextBox)sender;
        if (e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0) box.SetValue(FollowingProperty, AtBottom(box));
        else if (IsFollowing(box)) box.ScrollToEnd();
    }

    public static void Replace(TextBox box, string text) => Change(box, () => box.Text = text);

    public static void Append(TextBox box, string text) => Change(box, () =>
    {
        box.AppendText(text);
        if (box.Text.Length > 64000) box.Text = box.Text[^64000..];
    });

    private static void Change(TextBox box, Action update)
    {
        bool following = IsFollowing(box);
        double offset = box.VerticalOffset;
        update();
        box.UpdateLayout();
        if (following) box.ScrollToEnd();
        else box.ScrollToVerticalOffset(offset);
        box.UpdateLayout();
        box.SetValue(FollowingProperty, following || AtBottom(box));
    }
}
