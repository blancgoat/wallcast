namespace Still;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var mutex = new Mutex(true, "Still.Wallpaper.SingleInstance", out var first);
        if (!first) { MessageBox.Show("이미 실행 중입니다. 시스템 트레이에서 Still을 열어 주세요."); return; }
        try { Application.Run(new MainForm()); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Still 시작 오류", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
}
