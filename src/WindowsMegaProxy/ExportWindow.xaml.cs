using System.Windows;

namespace WindowsMegaProxy;

public partial class ExportWindow : Window
{
    public bool IncludePasswords => PasswordsBox.IsChecked == true;
    public bool IncludePrivateKeys => PrivateKeysBox.IsChecked == true;
    public bool ProxyList => FormatBox.SelectedIndex == 1;
    public ExportWindow()
    {
        InitializeComponent();
        FormatBox.SelectionChanged += (_, _) => PrivateKeysBox.IsEnabled = !ProxyList;
    }
    private void ExportClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
