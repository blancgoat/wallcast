using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Wallcast;

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

    // The window covers exactly the area the picture will occupy, not the whole monitor. Anything the
    // window does not cover is still Explorer's own wallpaper, which is how the surround stays the
    // user's wallpaper instead of a black bar.
    public void Attach(Screen screen, Rectangle? area = null)
    {
        var bounds = area ?? screen.Bounds;
        var progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero) throw new InvalidOperationException("Could not find the Windows desktop.");
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
        if (desktop == IntPtr.Zero) throw new InvalidOperationException("Could not create the desktop video layer. Restart Explorer and try again.");

        var handle = Handle;
        var style = GetWindowLongPtr(handle, -16).ToInt64();
        SetWindowLongPtr(handle, -16, new IntPtr((style & ~0x80000000L) | 0x40000000L));
        Marshal.SetLastPInvokeError(0);
        if (SetParent(handle, desktop) == IntPtr.Zero && Marshal.GetLastPInvokeError() != 0) throw new Win32Exception();
        var point = new POINT { X = bounds.X, Y = bounds.Y };
        MapWindowPoints(IntPtr.Zero, desktop, ref point, 1);
        if (!SetWindowPos(handle, IntPtr.Zero, point.X, point.Y, bounds.Width, bounds.Height, 0x0014)) throw new Win32Exception();
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
