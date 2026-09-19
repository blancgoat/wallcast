using LibVLCSharp.Shared;
using Wallcast;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--ui-preview"))
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                using var form = new MainForm();
                form.Show();
                // Capture is the default input, so the capture settings are already the ones on show.
                Application.DoEvents();
                using var preview = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(preview, new Rectangle(Point.Empty, preview.Size));
                preview.Save("artifacts/settings-preview.png", ImageFormat.Png);
                Console.WriteLine("PASS: Capture settings form rendered");
                return 0;
            }
            // Guards the regression this mode was written for: frames can arrive and the control can
            // paint while nothing reaches the screen, because Explorer's desktop window composites no
            // GDI output. Only a screen grab of the real desktop proves the wallpaper is live.
            if (args.Contains("--desktop"))
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                Bitmap? baseline = null;
                var name = args.SkipWhile(a => a != "--desktop").Skip(1).FirstOrDefault(a => !a.StartsWith("--")) ?? CaptureDevices.Enumerate()[0];
                var chosen = args.SkipWhile(a => a != "--aspect").Skip(1).FirstOrDefault();
                var res = args.SkipWhile(a => a != "--resolution").Skip(1).FirstOrDefault();
                var custom = args.SkipWhile(a => a != "--custom").Skip(1).FirstOrDefault(a => !a.StartsWith("--"));
                var mode = args.Contains("--squeeze") ? CaptureOptions.StretchShape : CaptureOptions.CropFit;
                var anchor = args.SkipWhile(a => a != "--anchor").Skip(1).FirstOrDefault(a => !a.StartsWith("--")) ?? "Center";
                var settings = new CaptureOptions(Resolution: res ?? CaptureOptions.Resolutions[0],
                    Aspect: custom is null ? chosen ?? CaptureOptions.Aspects[0] : CaptureOptions.Custom,
                    CustomSize: custom ?? "1920x1080", CustomMode: mode, Anchor: anchor).Normalize();
                Console.WriteLine($"AREA: aspect={settings.Aspect} custom={settings.CustomSize}/{settings.CustomMode}/{settings.Anchor} crop={settings.Crop} output={settings.OutputSize} -> {settings.Fit(Screen.PrimaryScreen!.Bounds.Size)}");
                var area = settings.Fit(Screen.PrimaryScreen!.Bounds.Size);
                area.Offset(Screen.PrimaryScreen!.Bounds.Location);
                var host = new DesktopHost();
                host.Attach(Screen.PrimaryScreen!, area);
                var capture = new CapturePlayback(name, 150, settings);
                string? trouble = null;
                capture.Failed += message => trouble ??= message;
                var surface = new CaptureSurface(capture);
                surface.Failed += message => trouble ??= message;
                host.Controls.Add(surface);
                capture.Start();
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var result = 2;
                // Startup costs a second of device negotiation and device creation, so rates are taken
                // over a steady-state window rather than from the beginning.
                double warmSeconds = 0; long warmFrames = 0, warmPresented = 0;
                var warm = new System.Windows.Forms.Timer { Interval = 3000 };
                warm.Tick += (_, _) =>
                {
                    warm.Stop();
                    warmSeconds = clock.Elapsed.TotalSeconds;
                    warmFrames = capture.Frames;
                    warmPresented = surface.Presented;
                };
                warm.Start();
                var probe = new System.Windows.Forms.Timer { Interval = 9000 };
                probe.Tick += (_, _) =>
                {
                    probe.Stop();
                    object? shell = null;
                    try
                    {
                        if (trouble is not null) { Console.WriteLine("FAIL: " + trouble); return; }
                        var seconds = clock.Elapsed.TotalSeconds - warmSeconds;
                        Console.WriteLine($"RATE {settings.Resolution}: {(capture.Frames - warmFrames) / seconds:F1} fps received, {(surface.Presented - warmPresented) / seconds:F1} fps on screen");
                        if (capture.Frames == 0) { Console.WriteLine("FAIL: no frames arrived from the device"); return; }
                        shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
                        shell!.GetType().InvokeMember("MinimizeAll", System.Reflection.BindingFlags.InvokeMethod, null, shell, null);
                        for (var i = 0; i < 30; i++) { Application.DoEvents(); Thread.Sleep(50); }
                        baseline = Baseline(host);
                        var bounds = Screen.PrimaryScreen!.Bounds;
                        var fitted = settings.Fit(bounds.Size);
                        // A moving source moves on while the screen is being grabbed, so the frame that
                        // is on screen is compared against several taken around the grab, not just one.
                        var references = Collect(capture, settings.OutputSize, 3);
                        // Windows that refuse to minimise would otherwise read as a broken renderer, so
                        // only the points where our own wallpaper window is on top are compared.
                        var visible = MaskWallpaper(fitted);
                        var covers = CoveringWindows();
                        bool visibleAt(int x, int y) => !covers.Any(cover => cover.Contains(x, y));
                        using var screen = new Bitmap(bounds.Width, bounds.Height);
                        using (var g = Graphics.FromImage(screen)) g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
                        references.AddRange(Collect(capture, settings.OutputSize, 3));
                        screen.Save("artifacts/desktop-capture.png", ImageFormat.Png);
                        if (references.Count == 0) { Console.WriteLine("FAIL: no frame to compare against"); return; }
                        var sampled = visible.Count(on => on);
                        if (sampled * 5 < visible.Length) { Console.WriteLine($"SKIP: only {sampled}/{visible.Length} of the wallpaper was uncovered, cannot verify"); return; }
                        var shown = SampleScreen(screen, fitted);
                        var matched = references.Max(reference => Matches(shown, reference, visible));
                        Console.WriteLine($"RESULT: {matched}/{sampled} uncovered desktop pixels match the captured frame (best of {references.Count})");
                        // The surround must still be the wallpaper the user had, not a black bar. A
                        // wallpaper that is itself black there would match either way, so the count of
                        // non-black baseline samples says whether this run could tell the difference.
                        if (baseline is not null && (fitted.Width < bounds.Width || fitted.Height < bounds.Height))
                        {
                            int same = 0, total = 0, telling = 0;
                            for (var y = 4; y < bounds.Height - 4; y += 37)
                                for (var x = 4; x < bounds.Width - 4; x += 37)
                                {
                                    if (fitted.Contains(x, y) || !visibleAt(x, y)) continue;
                                    var was = baseline.GetPixel(x, y);
                                    var now = screen.GetPixel(x, y);
                                    total++;
                                    if (was.R + was.G + was.B > 40) telling++;
                                    if (Math.Abs(was.R - now.R) + Math.Abs(was.G - now.G) + Math.Abs(was.B - now.B) <= 30) same++;
                                }
                            Console.WriteLine($"SURROUND: {same}/{total} match the bare desktop ({telling} of them not black to begin with)");
                            if (total > 0 && same * 10 < total * 9) { Console.WriteLine("FAIL: the surround no longer shows the original wallpaper"); return; }
                        }
                        if (matched * 10 < sampled * 9) { Console.WriteLine("FAIL: the desktop does not show the capture"); return; }
                        Console.WriteLine("PASS: capture is live on the desktop (artifacts/desktop-capture.png)");
                        result = 0;
                    }
                    catch (Exception ex) { Console.WriteLine("FAIL: " + ex.Message); }
                    finally
                    {
                        try { shell?.GetType().InvokeMember("UndoMinimizeALL", System.Reflection.BindingFlags.InvokeMethod, null, shell, null); } catch { }
                        host.Dispose();
                        capture.Dispose();
                        Application.Exit();
                    }
                };
                probe.Start();
                Application.Run();
                return result;
            }
            if (args.Contains("--bench"))
            {
                var name = args.SkipWhile(a => a != "--bench").Skip(1).FirstOrDefault(a => !a.StartsWith("--")) ?? CaptureDevices.Enumerate()[0];
                foreach (var res in new[] { "1920x1080", "2560x1440", "3840x2160" })
                {
                    var settings = new CaptureOptions(Resolution: res).Normalize();
                    using var capture = new CapturePlayback(name, 150, settings);
                    string? trouble = null;
                    capture.Failed += m => trouble ??= m;
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    capture.Start();
                    while (watch.Elapsed.TotalSeconds < 3) Thread.Sleep(50);
                    var warm = capture.Frames;
                    var mark = watch.Elapsed.TotalSeconds;
                    while (watch.Elapsed.TotalSeconds < 9) Thread.Sleep(50);
                    var rate = (capture.Frames - warm) / (watch.Elapsed.TotalSeconds - mark);
                    var mb = (double)settings.FrameSize.Width * settings.FrameSize.Height * 4 * rate / (1024 * 1024);
                    Console.WriteLine($"PIPE {res}: {rate:F1} fps  {mb:F0} MB/s{(trouble is null ? "" : "  TROUBLE: " + trouble)}");
                    Console.WriteLine($"   engine: {capture.InputDescription}");
                }
                return 0;
            }
            Core.Initialize();
            using var engine = new LibVLC("--no-video-title-show", "--vout=dummy", "--aout=dummy");
            Console.WriteLine("PASS: Native VLC loaded " + engine.Version);
            var devices = CaptureDevices.Enumerate();
            Console.WriteLine($"PASS: DirectShow enumeration ({devices.Count} devices)");
            foreach (var device in devices) Console.WriteLine("DEVICE: " + device);
            if (args.Length > 0 && args[0] == "--capture")
            {
                var captureOptions = new CaptureOptions(DynamicRange: args.Contains("--pq") ? CaptureOptions.DynamicRanges[1] : args.Contains("--hlg") ? CaptureOptions.DynamicRanges[2] : "SDR");
                using var capture = new CapturePlayback(args.Length > 1 ? args[1] : devices[0], 150, captureOptions);
                string? failure = null;
                capture.Failed += message => { failure = message; Console.WriteLine(message); };
                capture.Start();
                Thread.Sleep(10000);
                var frame = capture.TakeFrame();
                if (frame is not null)
                {
                    try
                    {
                        if (args.Contains("--snapshot"))
                        {
                            var size = capture.Options.FrameSize;
                            using var image = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppRgb);
                            var data = image.LockBits(new Rectangle(Point.Empty, size), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                            try { Marshal.Copy(frame, 0, data.Scan0, size.Width * size.Height * 4); }
                            finally { image.UnlockBits(data); }
                            image.Save(captureOptions.DynamicRange == "SDR" ? "artifacts/capture-nv12-rec709.png" : "artifacts/capture-hdr-tonemapped.png", ImageFormat.Png);
                        }
                    }
                    finally { CapturePlayback.ReturnFrame(frame); }
                }
                Console.WriteLine($"RESULT: NV12 / Rec.709 / limited / 1920x1080, frames={capture.Frames}");
                Console.WriteLine($"MODE: {captureOptions.DynamicRange}; INPUT: {capture.InputDescription}");
                return capture.Frames > 0 && failure is null ? 0 : 2;
            }
            try { using var missing = new VideoSource("nonexistent-video.mp4").Open(engine); throw new Exception("Missing file accepted"); }
            catch (FileNotFoundException) { Console.WriteLine("PASS: Missing file rejected"); }
            try { new CaptureOptions().CreateStartInfo("", 150, "pipe:1"); throw new Exception("Empty device accepted"); }
            catch (InvalidOperationException) { Console.WriteLine("PASS: Empty device rejected"); }
            var screen = new Size(3840, 2160);
            if (new CaptureOptions().Fit(screen) != new Rectangle(0, 0, 3840, 2160)) throw new Exception("Uncropped geometry distorted");
            // A 4:3 source pillarboxed into a 3840x2160 frame has exactly 480px of bar each side.
            var pillar = new CaptureOptions(Resolution: "3840x2160", Aspect: CaptureOptions.Custom, CustomSize: "2732x2048");
            if (pillar.Crop != new Rectangle(480, 0, 2880, 2160)) throw new Exception("Pillarbox not cropped off: " + pillar.Crop);
            if (pillar.Fit(screen) != new Rectangle(480, 0, 2880, 2160)) throw new Exception("Cropped picture not placed 1:1");
            if (!pillar.ConversionFilter.StartsWith("crop=2880:2160:480:0,")) throw new Exception("Crop missing from the filter chain");
            // The anchor moves the picture on the monitor; the cut itself always stays centred, because
            // that is where the bars are. A 4:3 picture on a 16:9 screen can only slide sideways.
            foreach (var (anchor, expected) in new[]
            {
                ("Top left", new Rectangle(0, 0, 2880, 2160)), ("Left", new Rectangle(0, 0, 2880, 2160)),
                ("Right", new Rectangle(960, 0, 2880, 2160)), ("Bottom right", new Rectangle(960, 0, 2880, 2160)),
                ("Center", new Rectangle(480, 0, 2880, 2160)), ("Top", new Rectangle(480, 0, 2880, 2160)),
            })
            {
                var placed = pillar with { Anchor = anchor };
                if (placed.Fit(screen) != expected) throw new Exception($"Anchor {anchor} put the picture at {placed.Fit(screen)}");
                if (placed.Crop != new Rectangle(480, 0, 2880, 2160)) throw new Exception($"Anchor {anchor} moved the crop");
            }
            // On a taller screen the room is vertical instead, so the anchor slides the other way.
            var square = new Size(2048, 2048);
            foreach (var (anchor, expected) in new[]
            {
                ("Top", new Rectangle(0, 0, 2048, 1536)), ("Center", new Rectangle(0, 256, 2048, 1536)),
                ("Bottom right", new Rectangle(0, 512, 2048, 1536)),
            })
                if ((pillar with { Anchor = anchor }).Fit(square) != expected)
                    throw new Exception($"Anchor {anchor} on a square screen gave {(pillar with { Anchor = anchor }).Fit(square)}");
            // Stretching an anamorphic frame crops nothing and only gives the picture its shape back.
            var squeezed = pillar with { CustomMode = CaptureOptions.StretchShape };
            if (squeezed.Crop != new Rectangle(0, 0, 3840, 2160)) throw new Exception("Stretch should not crop");
            if (squeezed.ConversionFilter.Contains("crop=")) throw new Exception("Stretch should not add a crop filter");
            // Screen placement needs no even rounding, so this lands on the exact 2732:2048 ratio.
            if (squeezed.Fit(screen) != new Rectangle(479, 0, 2881, 2160)) throw new Exception("Stretch did not reshape to the custom aspect: " + squeezed.Fit(screen));
            Console.WriteLine("PASS: pillarbox crop, screen anchors and anamorphic stretch");
            TestColors();
            using var control = new Control();
            using var playback = new Playback(control);
            playback.Stop(); playback.Stop();
            Console.WriteLine("PASS: Idle playback cleanup is repeatable");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static byte[] ConvertNv12(byte y, byte u, byte v, CaptureOptions options)
    {
        var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "capture", "ffmpeg.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-v", "error", "-f", "rawvideo", "-pix_fmt", "nv12", "-s", "2x2", "-i", "pipe:0", "-vf", options.ConversionFilter, "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1" }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var errors = process.StandardError.ReadToEndAsync();
        process.StandardInput.BaseStream.Write(new byte[] { y, y, y, y, u, v });
        process.StandardInput.Close();
        using var output = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(output);
        process.WaitForExit();
        if (process.ExitCode != 0) throw new Exception(errors.Result);
        return output.ToArray();
    }

    private static void TestColors()
    {
        var options = new CaptureOptions();
        var red = ConvertNv12(63, 102, 240, options);
        if (red.Length != 16 || red[2] < 245 || red[0] > 8 || red[1] > 8) throw new Exception("Rec.709 red channel regression");
        var blue = ConvertNv12(32, 240, 118, options);
        if (blue[0] < 245 || blue[1] > 8 || blue[2] > 8) throw new Exception("Rec.709 blue channel regression");
        var black = ConvertNv12(16, 128, 128, options);
        var white = ConvertNv12(235, 128, 128, options);
        if (black[0] > 2 || white[0] < 253) throw new Exception("Limited range regression");
        var full = ConvertNv12(16, 128, 128, options with { ColorRange = "Full" });
        if (Math.Abs(full[0] - 16) > 2) throw new Exception("Full range selection ignored");
        var rec601 = ConvertNv12(63, 102, 240, options with { ColorSpace = "Rec.601" });
        if (Math.Abs(red[2] - rec601[2]) < 10) throw new Exception("Color matrix selection ignored");
        Console.WriteLine("PASS: NV12 red/blue channels, limited/full range, Rec.709/601 matrix conversion");
        foreach (var mode in CaptureOptions.DynamicRanges.Skip(1))
        {
            var hdr = options with { DynamicRange = mode };
            var dark = ConvertNv12(16, 128, 128, hdr);
            var middle = ConvertNv12(128, 128, 128, hdr);
            var bright = ConvertNv12(200, 128, 128, hdr);
            if (dark.Length != 16 || dark[0] > 3 || middle[0] <= dark[0] || bright[0] <= middle[0]) throw new Exception("HDR luminance mapping regression: " + mode);
            if (Math.Abs(middle[0] - middle[2]) > 2) throw new Exception("HDR neutral gray is tinted: " + mode);
            var peakSample = ConvertNv12(170, 128, 128, hdr);
            var alternate = ConvertNv12(170, 128, 128, hdr with { HdrPeak = "4000" });
            if (peakSample[0] == alternate[0]) throw new Exception("HDR peak selection ignored: " + mode);
        }
        Console.WriteLine("PASS: PQ/HLG tone mapping, neutral gray, highlight ordering and peak selection");
    }

    // WindowFromPoint cannot find our wallpaper window, because Explorer's WorkerW above it is disabled
    // and that call skips disabled windows. So the covered area is worked out from every ordinary
    // window still standing on the desktop instead.
    private static List<Rectangle> CoveringWindows()
    {
        var covers = new List<Rectangle>();
        var name = new System.Text.StringBuilder(64);
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || IsIconic(window)) return true;
            GetClassName(window, name, name.Capacity);
            if (name.ToString() is "Progman" or "WorkerW") return true;
            if (DwmGetWindowAttribute(window, 14, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            if (GetWindowRect(window, out var box) && box.Right > box.Left && box.Bottom > box.Top)
                covers.Add(Rectangle.FromLTRB(box.Left, box.Top, box.Right, box.Bottom));
            return true;
        }, IntPtr.Zero);
        return covers;
    }

    private static bool[] MaskWallpaper(Rectangle target)
    {
        var covers = CoveringWindows();
        var mask = new bool[(Grid - First) * (Grid - First)];
        var at = 0;
        for (var y = First; y < Grid; y++)
            for (var x = First; x < Grid; x++)
            {
                var point = new Point(target.X + target.Width * x / Grid, target.Y + target.Height * y / Grid);
                mask[at++] = !covers.Any(cover => cover.Contains(point));
            }
        return mask;
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out RECT box);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, System.Text.StringBuilder name, int max);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);

    // Frames are compared as a small grid of samples rather than whole bitmaps, so several of them can
    // be held around the moment of the screen grab without copying megabytes each time.
    private const int Grid = 100, First = 8;

    private static List<int[]> Collect(CapturePlayback capture, Size size, int count)
    {
        var taken = new List<int[]>();
        for (var i = 0; i < count * 40 && taken.Count < count; i++)
        {
            var frame = capture.TakeFrame();
            if (frame is null) { Thread.Sleep(5); continue; }
            try { taken.Add(SampleFrame(frame, size)); }
            finally { CapturePlayback.ReturnFrame(frame); }
        }
        return taken;
    }

    private static int[] SampleFrame(byte[] frame, Size size)
    {
        var samples = new int[(Grid - First) * (Grid - First)];
        var at = 0;
        for (var y = First; y < Grid; y++)
            for (var x = First; x < Grid; x++)
            {
                var offset = size.Height * y / Grid * size.Width * 4 + size.Width * x / Grid * 4;
                samples[at++] = (frame[offset + 2] << 16) | (frame[offset + 1] << 8) | frame[offset];
            }
        return samples;
    }

    private static int[] SampleScreen(Bitmap screen, Rectangle target)
    {
        var samples = new int[(Grid - First) * (Grid - First)];
        var at = 0;
        for (var y = First; y < Grid; y++)
            for (var x = First; x < Grid; x++)
            {
                var sx = Math.Min(screen.Width - 1, target.X + target.Width * x / Grid);
                var sy = Math.Min(screen.Height - 1, target.Y + target.Height * y / Grid);
                var shown = screen.GetPixel(sx, sy);
                samples[at++] = (shown.R << 16) | (shown.G << 8) | shown.B;
            }
        return samples;
    }

    private static int Matches(int[] shown, int[] reference, bool[] visible)
    {
        var matched = 0;
        for (var i = 0; i < shown.Length; i++)
        {
            if (!visible[i]) continue;
            // The frame is scaled on the GPU and sampled a moment apart, so allow some drift.
            var drift = Math.Abs((shown[i] >> 16 & 255) - (reference[i] >> 16 & 255))
                + Math.Abs((shown[i] >> 8 & 255) - (reference[i] >> 8 & 255))
                + Math.Abs((shown[i] & 255) - (reference[i] & 255));
            if (drift <= 60) matched++;
        }
        return matched;
    }

    // What the desktop looks like with our window hidden, so the surround can be compared against it.
    private static Bitmap Baseline(DesktopHost host)
    {
        host.Visible = false;
        for (var i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(40); }
        var bounds = Screen.PrimaryScreen!.Bounds;
        var shot = new Bitmap(bounds.Width, bounds.Height);
        using (var g = Graphics.FromImage(shot)) g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        host.Visible = true;
        for (var i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(40); }
        return shot;
    }
}
