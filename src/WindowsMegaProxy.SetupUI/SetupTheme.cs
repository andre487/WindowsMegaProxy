using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

internal static class SetupTheme
{
    internal static void Apply(Form window)
    {
        if (SystemInformation.HighContrast) return;
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        bool dark = key?.GetValue("AppsUseLightTheme") is 0;
        Color background = dark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(243, 243, 243);
        Color foreground = dark ? Color.FromArgb(242, 242, 242) : Color.FromArgb(26, 26, 26);
        void Visit(Control control)
        {
            control.BackColor = background;
            control.ForeColor = foreground;
            if (control is LinkLabel link) link.LinkColor = link.ActiveLinkColor = dark ? Color.FromArgb(153, 204, 255) : Color.FromArgb(0, 95, 184);
            if (control.Tag as string == "Explanation")
                control.ForeColor = dark ? Color.FromArgb(180, 180, 180) : Color.FromArgb(95, 95, 95);
            if (control is Button button)
            {
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderColor = dark ? Color.FromArgb(95, 95, 95) : Color.FromArgb(170, 170, 170);
                button.BackColor = dark ? Color.FromArgb(48, 48, 48) : Color.White;
            }
            foreach (Control child in control.Controls) Visit(child);
        }
        Visit(window);
        int enabled = dark ? 1 : 0;
        // Unsupported DWM attributes are harmless on older supported Windows versions.
        DwmSetWindowAttribute(window.Handle, 20, ref enabled, sizeof(int));
    }

    internal static void ApplyChoices(System.Windows.ResourceDictionary resources)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        bool dark = key?.GetValue("AppsUseLightTheme") is 0;
        void Set(string name, string light, string night) => resources[name] =
            new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(dark ? night : light));
        Set("Background", "#F3F3F3", "#202020");
        Set("Foreground", "#1A1A1A", "#F2F2F2");
        Set("Secondary", "#5F5F5F", "#B4B4B4");
        Set("Border", "#D0D0D0", "#505050");
        Set("CardBackground", "#FFFFFF", "#2D2D2D");
        Set("HoverBackground", "#F9F9F9", "#383838");
        Set("SelectedBackground", "#EDF4FA", "#273847");
        Set("Accent", "#005FB8", "#99CCFF");
        Set("AccentForeground", "#FFFFFF", "#202020");
        if (!SystemInformation.HighContrast) return;
        resources["Background"] = resources["CardBackground"] = resources["HoverBackground"] = resources["SelectedBackground"] = System.Windows.SystemColors.WindowBrush;
        resources["Foreground"] = resources["Secondary"] = resources["Border"] = System.Windows.SystemColors.WindowTextBrush;
        resources["Accent"] = System.Windows.SystemColors.HighlightBrush;
        resources["AccentForeground"] = System.Windows.SystemColors.HighlightTextBrush;
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
