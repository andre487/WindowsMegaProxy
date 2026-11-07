using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace WindowsMegaProxy;

public static class LogAutoScroll
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached("Enabled", typeof(bool),
        typeof(LogAutoScroll), new PropertyMetadata(false, EnabledChanged));
    private static readonly DependencyProperty FollowingProperty = DependencyProperty.RegisterAttached("Following", typeof(bool),
        typeof(LogAutoScroll), new PropertyMetadata(true));
    private static readonly DependencyProperty ScrollProperty = DependencyProperty.RegisterAttached("Scroll", typeof(DispatcherOperation), typeof(LogAutoScroll));
    private static readonly DependencyProperty ChangingProperty = DependencyProperty.RegisterAttached("Changing", typeof(bool), typeof(LogAutoScroll), new PropertyMetadata(false));

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
        if ((bool)box.GetValue(ChangingProperty)) return;
        if (e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0) box.SetValue(FollowingProperty, AtBottom(box));
        else if (IsFollowing(box)) box.ScrollToEnd();
    }

    public static void Replace(TextBox box, string text)
    {
        if (box.Text == text) return;
        string previous = box.Text;
        int overlap = Overlap(previous, text);
        Change(box, () =>
        {
            int selection = box.SelectionStart, length = box.SelectionLength;
            int removed = previous.Length - overlap;
            box.BeginChange();
            try
            {
                if (overlap == 0) box.Text = text;
                else
                {
                    if (removed > 0) { box.Select(0, removed); box.SelectedText = ""; }
                    box.AppendText(text[overlap..]);
                }
                box.Select(Math.Max(0, selection - removed), Math.Min(length, Math.Max(0, text.Length - Math.Max(0, selection - removed))));
            }
            finally { box.EndChange(); }
        });
    }

    private static int Overlap(string previous, string text)
    {
        if (text.Length == 0) return 0;
        if (text.StartsWith(previous, StringComparison.Ordinal)) return previous.Length;
        var prefix = new int[text.Length];
        for (int i = 1, matched = 0; i < text.Length; i++)
        {
            while (matched > 0 && text[i] != text[matched]) matched = prefix[matched - 1];
            if (text[i] == text[matched]) matched++;
            prefix[i] = matched;
        }
        int count = 0;
        foreach (char character in previous)
        {
            while (count > 0 && (count == text.Length || character != text[count])) count = prefix[count - 1];
            if (character == text[count]) count++;
        }
        return count;
    }

    public static void Append(TextBox box, string text)
    {
        string combined = box.Text + text;
        Replace(box, combined.Length > 64000 ? combined[^64000..] : combined);
    }

    private static void Change(TextBox box, Action update)
    {
        bool following = IsFollowing(box);
        double offset = box.VerticalOffset;
        box.SetValue(ChangingProperty, true);
        update();
        (box.GetValue(ScrollProperty) as DispatcherOperation)?.Abort();
        box.SetValue(ScrollProperty, box.Dispatcher.BeginInvoke(() =>
        {
            if (following) box.ScrollToEnd();
            else box.ScrollToVerticalOffset(offset);
            box.SetValue(FollowingProperty, following);
            box.SetValue(ChangingProperty, false);
        }, DispatcherPriority.Loaded));
    }
}
