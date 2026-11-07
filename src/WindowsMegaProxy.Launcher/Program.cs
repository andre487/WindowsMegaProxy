using System;
using System.Runtime.InteropServices;
using System.Security.Principal;
using WindowsMegaProxy;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        try
        {
            dynamic scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
            scheduler.Connect();
            string task = AppIdentity.Name + ".User." + WindowsIdentity.GetCurrent().User!.Value;
            scheduler.GetFolder(@"\").GetTask(task).Run(null);
            return 0;
        }
        catch (Exception error)
        {
            MessageBox(IntPtr.Zero, "Could not start MegaProxy. Repair the current-user installation.\n\n" + error.Message, "MegaProxy", 0x10);
            return 1;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr owner, string text, string caption, uint flags);
}
