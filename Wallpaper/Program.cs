namespace Still;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var mutex = new Mutex(true, "Still.Wallpaper.SingleInstance", out var first);
        if (!first) { MessageBox.Show("Still is already running. Open it from the system tray."); return; }
        try { Application.Run(new MainForm()); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Still startup error", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
}
