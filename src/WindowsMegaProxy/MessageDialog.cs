using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WindowsMegaProxy;

public sealed class MessageDialog : Window
{
    public MessageDialog(Window? owner, string message, string title, bool confirmation = true)
    {
        Owner = owner;
        Title = title;
        Width = 460;
        MaxWidth = SystemParameters.WorkArea.Width;
        MaxHeight = SystemParameters.WorkArea.Height;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Segoe UI Variable, Segoe UI");
        FontSize = 14;
        UseLayoutRounding = true;
#pragma warning disable WPF0001
        ThemeMode = owner?.ThemeMode ?? Application.Current.ThemeMode;
#pragma warning restore WPF0001
        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;
        root.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }
        });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };
        Grid.SetRow(actions, 1);
        root.Children.Add(actions);
        var yes = new Button { Content = Ui.T("Yes"), MinWidth = 96, Margin = new Thickness(0, 0, 8, 0) };
        var no = new Button { Content = confirmation ? Ui.T("No") : "OK", MinWidth = 96, IsCancel = true, IsDefault = true };
        yes.Click += (_, _) => DialogResult = true;
        no.Click += (_, _) => DialogResult = false;
        if (confirmation) actions.Children.Add(yes);
        actions.Children.Add(no);
        Loaded += (_, _) => no.Focus();
    }

    public static void ShowError(Window? owner, string message, string title) => new MessageDialog(owner, message, title, confirmation: false).ShowDialog();

    public static bool Confirm(Window? owner, string message, string title) => new MessageDialog(owner, message, title).ShowDialog() == true;
}
