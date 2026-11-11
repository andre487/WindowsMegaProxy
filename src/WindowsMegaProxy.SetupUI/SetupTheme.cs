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
            if (control is ComboBox combo)
            {
                combo.FlatStyle = FlatStyle.Flat;
                combo.DrawMode = DrawMode.OwnerDrawFixed;
                combo.DrawItem += (_, args) =>
                {
                    using var brush = new SolidBrush(background);
                    args.Graphics.FillRectangle(brush, args.Bounds);
                    if (args.Index >= 0) TextRenderer.DrawText(args.Graphics, combo.Items[args.Index].ToString(), combo.Font,
                        args.Bounds, foreground, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                    args.DrawFocusRectangle();
                };
            }
            if (control is Button button)
            {
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderColor = dark ? Color.FromArgb(95, 95, 95) : Color.FromArgb(170, 170, 170);
                button.BackColor = dark ? Color.FromArgb(48, 48, 48) : Color.White;
            }
            foreach (Control child in control.Controls) Visit(child);
        }
        Visit(window);
        window.HandleCreated += (_, _) =>
        {
            int enabled = dark ? 1 : 0;
            // Unsupported DWM attributes are harmless on older supported Windows versions.
            DwmSetWindowAttribute(window.Handle, 20, ref enabled, sizeof(int));
        };
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
