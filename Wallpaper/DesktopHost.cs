using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Still;

internal sealed class DesktopHost : Form
{
    private IntPtr desktop;
    public DesktopHost()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        BackColor = Color.Black;
        StartPosition = FormStartPosition.Manual;
    }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get { var p = base.CreateParams; p.ExStyle |= 0x08000080; return p; }
    }

    public void Attach(Screen screen)
    {
        var progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero) throw new InvalidOperationException("Windows 바탕화면을 찾을 수 없습니다.");
        SendMessageTimeout(progman, 0x052C, new IntPtr(0xD), new IntPtr(1), 2, 1000, out _);
        desktop = IntPtr.Zero;
        EnumWindows((window, _) =>
        {
            if (FindWindowEx(window, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                desktop = FindWindowEx(IntPtr.Zero, window, "WorkerW", null);
            return desktop == IntPtr.Zero;
        }, IntPtr.Zero);
        // Some Explorer versions keep the icon view and wallpaper worker under Progman.
        if (desktop == IntPtr.Zero) desktop = FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
        if (desktop == IntPtr.Zero) throw new InvalidOperationException("바탕화면 영상 영역을 만들 수 없습니다. Explorer를 다시 시작한 뒤 시도해 주세요.");

        var handle = Handle;
        var style = GetWindowLongPtr(handle, -16).ToInt64();
        SetWindowLongPtr(handle, -16, new IntPtr((style & ~0x80000000L) | 0x40000000L));
        Marshal.SetLastPInvokeError(0);
        if (SetParent(handle, desktop) == IntPtr.Zero && Marshal.GetLastPInvokeError() != 0) throw new Win32Exception();
        var point = new POINT { X = screen.Bounds.X, Y = screen.Bounds.Y };
        MapWindowPoints(IntPtr.Zero, desktop, ref point, 1);
        if (!SetWindowPos(handle, IntPtr.Zero, point.X, point.Y, screen.Bounds.Width, screen.Bounds.Height, 0x0014)) throw new Win32Exception();
        Show();
    }

    public bool IsDesktopAlive => IsWindow(desktop);
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr parameter);
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string name, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string name, string? title);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern int MapWindowPoints(IntPtr from, IntPtr to, ref POINT point, uint count);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
}
