using System.Windows;

namespace WindowsMegaProxy;

public partial class ExportWindow : Window
{
    public bool IncludePasswords => PasswordsBox.IsChecked == true;
    public bool IncludePrivateKeys => PrivateKeysBox.IsChecked == true;
    public ExportWindow() => InitializeComponent();
    private void ExportClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
