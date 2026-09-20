using System.Runtime.InteropServices;

namespace Wallcast;

// Catches the mouse where the picture is, before the desktop gets it.
//
// The picture sits behind the icon layer, so Explorer's own list view is what a click on it actually
// lands on - nothing reaches our window at all. The only place to intercept that is a low-level hook,
// which sees every mouse message on the way past and can swallow the ones over the picture.
//
// Swallowing is the point: a click that went on to Explorer as well would clear the icon selection
// or start a rubber band at the same time as tapping the device. Everything outside the picture, and
// everything over a real window, is left alone.
internal sealed class DesktopClicks : IDisposable
{
    private const int MouseHook = 14, HookAction = 0;
    private const int Move = 0x0200, LeftDown = 0x0201, LeftUp = 0x0202,
        RightDown = 0x0204, RightUp = 0x0205, Wheel = 0x020A;

    // The hook is called by Windows, so the delegate has to outlive the call that installs it.
    private readonly LowLevelMouseProc callback;
    private IntPtr hook;
    private bool dragging;
    // Buttons whose press was handed to Explorer. The release has to follow the press to the same
    // place, or Explorer is left believing a button is still down.
    private int explorers;
    // A wheel does not arrive a notch at a time. A high-resolution wheel or a precision touchpad
    // sends fractions of one - 40, 30, 12 - and dividing each of those by a notch gives zero, so a
    // slow scroll turned into nothing at all while a fast flick worked. What is left over is kept
    // and added to the next one.
    private int notches;

    /// <summary>The picture's rectangle in screen pixels. Empty means the hook does nothing.</summary>
    public Rectangle Area { get; set; }

    /// <summary>When set, only a click with Alt held is taken, and every other click still belongs
    /// to the desktop. It is the difference between a wallpaper you can poke and a wallpaper that
    /// has quietly stopped being a desktop.</summary>
    public bool RequireAlt { get; set; }

    public bool Listening => hook != IntPtr.Zero;

    /// <summary>Where the pointer should go, as a fraction of the picture, and what to do there.</summary>
    public event Action<PointF>? Moved;
    public event Action<PointF, int, bool>? Clicked;
    public event Action<PointF, int>? Scrolled;
    /// <summary>Every message the hook decides is ours, for when a click does not arrive.</summary>
    public event Action<string>? Hooked;

    public DesktopClicks() => callback = Intercept;

    public void Start()
    {
        if (hook != IntPtr.Zero) return;
        hook = SetWindowsHookEx(MouseHook, callback, GetModuleHandle(null), 0);
        if (Pointer.Tracing) Hooked?.Invoke($"hook installed={hook != IntPtr.Zero} area={Area} alt={RequireAlt}");
        if (hook == IntPtr.Zero) throw new InvalidOperationException("Windows would not allow the mouse hook.");
    }

    public void Stop()
    {
        if (hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(hook);
        hook = IntPtr.Zero;
        dragging = false;
        explorers = 0;
        notches = 0;
        Observed = 0;
    }

    private IntPtr Intercept(int code, IntPtr message, IntPtr data)
    {
        if (code < HookAction) return CallNextHookEx(hook, code, message, data);
        var mouse = Marshal.PtrToStructure<MouseInput>(data);
        var at = new Point(mouse.X, mouse.Y);
        var action = (int)message;

        // A drag that began on the picture keeps going wherever it wanders, the way a drag does
        // everywhere else. Anything else outside the picture is not ours.
        var mine = Area.Contains(at) && OverTheDesktop(at) && (!RequireAlt || AltHeld) || dragging && action != Wheel;
        // Buttons and the wheel are rare enough to say something about even when they are not ours;
        // a click that vanishes is otherwise indistinguishable from a hook that never ran.
        if (action != Move && Pointer.Tracing)
            Hooked?.Invoke($"raw 0x{action:X3} at {at.X},{at.Y} mine={mine} area={Area} inside={Area.Contains(at)} desktop={OverTheDesktop(at)}");
        if (!mine) return CallNextHookEx(hook, code, message, data);

        var where = Fraction(at);
        if (Pointer.Tracing) Hooked?.Invoke($"hook 0x{action:X3} at {at.X},{at.Y} dragging={dragging}");
        switch (action)
        {
            // Never swallowed. Blocking a move in a low-level hook stops the cursor itself - the
            // pointer freezes on this screen the moment a drag begins, which is worse than anything
            // it was meant to prevent. There is nothing to prevent anyway: the button-down was
            // swallowed, so Explorer does not believe a button is down and starts no rubber band.
            case Move: Moved?.Invoke(where); return CallNextHookEx(hook, code, message, data);

            case LeftDown or RightDown:
            {
                var button = action == LeftDown ? 1 : 2;
                Observed |= button;
                if (OnIcon(at)) { explorers |= button; return CallNextHookEx(hook, code, message, data); }
                if (button == 1) dragging = true;
                Clicked?.Invoke(where, button, true);
                return 1;
            }

            case LeftUp or RightUp:
            {
                var button = action == LeftUp ? 1 : 2;
                Observed &= ~button;
                if (button == 1) dragging = false;
                if ((explorers & button) != 0) { explorers &= ~button; return CallNextHookEx(hook, code, message, data); }
                Clicked?.Invoke(where, button, false);
                return 1;
            }

            // The wheel delta is in the high word of mouseData, 120 to a click.
            case Wheel:
            {
                if (OnIcon(at)) return CallNextHookEx(hook, code, message, data);
                // The delta is in the high word of mouseData, 120 to a notch.
                notches += (short)(mouse.Data >> 16);
                var whole = notches / 120;
                notches -= whole * 120;
                if (whole != 0) Scrolled?.Invoke(where, whole);
                return 1;
            }
            default: return CallNextHookEx(hook, code, message, data);
        }
    }

    /// <summary>Where in the picture, as 0..1 across and down, which is what an absolute pointer
    /// wants and what keeps this independent of the monitor's resolution.</summary>
    private PointF Fraction(Point at) => new(
        Math.Clamp((at.X - Area.X) / (float)Math.Max(1, Area.Width), 0, 1),
        Math.Clamp((at.Y - Area.Y) / (float)Math.Max(1, Area.Height), 0, 1));

    /// <summary>The picture's rectangle is also underneath any window parked over it, and a click on
    /// that window is the window's. Only the desktop's own layers count as the picture.</summary>
    private static bool OverTheDesktop(Point at)
    {
        var window = WindowFromPoint(at);
        if (window == IntPtr.Zero) return false;
        var name = new System.Text.StringBuilder(64);
        GetClassName(window, name, name.Capacity);
        return name.ToString() is "Progman" or "WorkerW" or "SHELLDLL_DefView" or "SysListView32";
    }

    /// <summary>Whether a desktop icon is under the point, in which case the click is the desktop's
    /// and not the device's. The icons are drawn over the picture and the picture is behind them, so
    /// without this the recycle bin stops being clickable the moment the wallpaper covers it.
    ///
    /// Explorer will answer this itself. The accessible object at a point comes back with the index
    /// of the item under it, or zero for the bare background - measured at 0.49ms, and only asked on
    /// a button, never on a move. The alternative was writing a hit-test structure into Explorer's
    /// own memory, which is the same thing every injector does and not what a wallpaper should be
    /// doing to the shell.</summary>
    private static bool OnIcon(Point at)
    {
        object? accessible = null;
        try
        {
            if (AccessibleObjectFromPoint(at, out accessible, out var child) != 0) return false;
            return child is int index && index > 0;
        }
        catch (Exception problem) when (problem is not OutOfMemoryException) { return false; }
        finally { if (accessible is not null) Marshal.ReleaseComObject(accessible); }
    }

    private static bool AltHeld => (GetKeyState(0x12) & 0x8000) != 0;

    /// <summary>Buttons the hook has watched go down without yet watching them come up.
    ///
    /// Not <c>GetAsyncKeyState</c>: presses over the picture are swallowed here, so Windows never
    /// records them and the system state says nothing is down for the whole of a perfectly ordinary
    /// hold. Asking it would cut every long press short. While the hook is installed it sees every
    /// button in the machine, so what it has watched is the closest thing to ground truth there is.</summary>
    public int Observed { get; private set; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public IntPtr Extra; }

    private delegate IntPtr LowLevelMouseProc(int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, LowLevelMouseProc callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point at);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, System.Text.StringBuilder name, int length);
    [DllImport("user32.dll")] private static extern short GetKeyState(int key);
    [DllImport("oleacc.dll")] private static extern int AccessibleObjectFromPoint(Point at,
        [MarshalAs(UnmanagedType.IUnknown)] out object accessible, [MarshalAs(UnmanagedType.Struct)] out object child);

    public void Dispose() => Stop();
}
