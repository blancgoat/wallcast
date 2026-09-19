namespace Wallcast;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var mutex = new Mutex(true, "Wallcast.SingleInstance", out var first);
        if (!first) { MessageBox.Show("Wallcast is already running. Open it from the system tray."); return; }
        try { Application.Run(new MainForm()); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Wallcast startup error", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
}
