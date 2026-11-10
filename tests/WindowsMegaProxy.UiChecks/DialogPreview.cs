using System.Windows;
using WindowsMegaProxy;

internal static partial class Program
{
    private static void PreviewNotifications()
    {
        SetCurrentProcessExplicitAppUserModelID(AppIdentity.Name);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        NotificationIdentity.Register();
        Ui.SetLanguage("ru");
        using var stream = Application.GetResourceStream(new System.Uri($"pack://application:,,,/{AppIdentity.Name};component/Assets/{AppIdentity.IconPrefix}tray.ico"))!.Stream;
        using var icon = new System.Drawing.Icon(stream);
        using var tray = new System.Windows.Forms.NotifyIcon { Icon = icon, Text = "MegaProxy — notification preview", Visible = true };
        var notices = new[]
        {
            ("MegaProxy", Ui.F("Update {0} is available. Click to open updates.", "0.0.99"), System.Windows.Forms.ToolTipIcon.Info),
            (Ui.T("Another MegaProxy version is running"), Ui.F("{0} is running. Click to choose which version to keep.", "MegaProxy DEV"), System.Windows.Forms.ToolTipIcon.Warning),
            ("MegaProxy", Ui.T("The core exited. TUN is disconnected."), System.Windows.Forms.ToolTipIcon.Warning),
            ("MegaProxy", Ui.T("Operation failed. Open the application window."), System.Windows.Forms.ToolTipIcon.Error)
        };
        var window = new Window { Title = "MegaProxy — просмотр уведомлений", Width = 440, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        UiTheme.Apply("ru", "system", window);
        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(24) };
        var status = new System.Windows.Controls.TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) };
        var next = new System.Windows.Controls.Button { Content = "Следующее уведомление" };
        panel.Children.Add(status); panel.Children.Add(next); window.Content = panel;
        int index = 0;
        void ShowNext()
        {
            if (index == notices.Length) { window.Close(); return; }
            var notice = notices[index++];
            status.Text = $"{index}/4 — {notice.Item2}\n\nЭто тест: никаких действий с приложением не выполняется.";
            next.Content = index == notices.Length ? "Завершить просмотр" : "Следующее уведомление";
            tray.ShowBalloonTip(10000, notice.Item1, notice.Item2, notice.Item3);
        }
        next.Click += (_, _) => ShowNext();
        tray.BalloonTipClicked += (_, _) => app.Dispatcher.BeginInvoke(ShowNext);
        window.ContentRendered += (_, _) => ShowNext();
        window.ShowDialog();
        tray.Visible = false;
        app.Shutdown();
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string id);

    private static void PreviewRemainingDialogs()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Ui.SetLanguage("ru");
        void Show(Window dialog)
        {
            UiTheme.Apply("ru", "system", dialog);
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.ShowDialog();
        }
        var profile = ProxyProfile.Create("Example proxy");
        profile.Proxy["host"] = "proxy.example";
        var current = new Settings { Profiles = [profile, ProxyProfile.Create("Old example profile")], ActiveProfileId = profile.Id };
        current.Profiles[1].Proxy["host"] = "old.example";
        var imported = current.Copy();
        imported.Profiles.RemoveAt(1);
        imported.Profiles[0].Proxy["port"] = 8443;
        Show(new ImportWindow(new ImportReview(ProfileTransfer.Parse(PortableConfiguration.Export(imported).ToJsonString()), current)));
        Show(new ExportWindow());
        Show(new SshHostKeyWindow(new SshHostKeyUpdate(profile.Id, profile.Name, "ssh.example", 22,
            "SHA256:EXAMPLE-OLD-FINGERPRINT", "SHA256:EXAMPLE-NEW-FINGERPRINT", "", "jump")));
        Show(ChannelConflict.CreateDialog());
        Show(new RuleWindow(new RoutingRule { Name = "Example rule", Source = "folder", Path = @"C:\Example\Apps", Destination = "example.com", Action = "proxy" }));
        Show(new ProfileWindow(profile));
        MessageDialog.ShowError(null, Ui.T("Could not open the application: ") + "Example error (preview)", "MegaProxy");
        MessageDialog.ShowError(null, "Example error (preview)", Ui.T("Disconnecting"));
        MessageDialog.ShowError(null, "Example validation error (preview)", Ui.T("Rule"));
        new Microsoft.Win32.OpenFileDialog { Filter = "Supported configurations|*.json;*.txt|All files|*.*" }.ShowDialog();
        new Microsoft.Win32.OpenFolderDialog().ShowDialog();
        new Microsoft.Win32.SaveFileDialog { FileName = "Example.json", Filter = "MegaProxy JSON|*.json" }.ShowDialog();
        app.Shutdown();
    }
}
