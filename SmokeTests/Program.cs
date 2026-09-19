using LibVLCSharp.Shared;
using Still;
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
                var field = typeof(MainForm).GetField("mode", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                ((ComboBox)field.GetValue(form)!).SelectedIndex = 1;
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
                var name = args.SkipWhile(a => a != "--desktop").Skip(1).FirstOrDefault(a => !a.StartsWith("--")) ?? CaptureDevices.Enumerate()[0];
                var chosen = args.SkipWhile(a => a != "--aspect").Skip(1).FirstOrDefault();
                var settings = new CaptureOptions(Aspect: chosen ?? CaptureOptions.Aspects[0]).Normalize();
                var host = new DesktopHost();
                host.Attach(Screen.PrimaryScreen!);
                var capture = new CapturePlayback(name, 150, settings);
                string? trouble = null;
                capture.Failed += message => trouble ??= message;
                var surface = new CaptureSurface(capture);
                surface.Failed += message => trouble ??= message;
                host.Controls.Add(surface);
                capture.Start();
                var result = 2;
                var probe = new System.Windows.Forms.Timer { Interval = 4000 };
                probe.Tick += (_, _) =>
                {
                    probe.Stop();
                    object? shell = null;
                    try
                    {
                        if (trouble is not null) { Console.WriteLine("FAIL: " + trouble); return; }
                        if (capture.Frames == 0) { Console.WriteLine("FAIL: no frames arrived from the device"); return; }
                        shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
                        shell!.GetType().InvokeMember("MinimizeAll", System.Reflection.BindingFlags.InvokeMethod, null, shell, null);
                        for (var i = 0; i < 30; i++) { Application.DoEvents(); Thread.Sleep(50); }
                        var bounds = Screen.PrimaryScreen!.Bounds;
                        using var screen = new Bitmap(bounds.Width, bounds.Height);
                        using (var g = Graphics.FromImage(screen)) g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
                        var expected = DesktopReference(capture, settings, bounds);
                        screen.Save("artifacts/desktop-capture.png", ImageFormat.Png);
                        if (expected is null) { Console.WriteLine("FAIL: no frame to compare against"); return; }
                        var (matched, sampled) = Compare(screen, expected, settings.Fit(bounds.Size));
                        Console.WriteLine($"RESULT: {matched}/{sampled} sampled desktop pixels match the captured frame");
                        var fitted = settings.Fit(bounds.Size);
                        if (fitted.X > 8)
                        {
                            var bar = screen.GetPixel(fitted.X / 2, bounds.Height / 2);
                            Console.WriteLine($"LETTERBOX: bar pixel {bar.R},{bar.G},{bar.B}");
                            if (bar.R + bar.G + bar.B > 30) { Console.WriteLine("FAIL: letterbox is not black"); return; }
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
            try { new CaptureOptions().CreateStartInfo("", 150); throw new Exception("Empty device accepted"); }
            catch (InvalidOperationException) { Console.WriteLine("PASS: Empty device rejected"); }
            if (new CaptureOptions().Fit(new Size(3840, 2160)) != new Rectangle(0, 0, 3840, 2160)) throw new Exception("16:9 geometry distorted");
            if (new CaptureOptions(Aspect: "4:3").Fit(new Size(3840, 2160)) != new Rectangle(480, 0, 2880, 2160)) throw new Exception("4:3 geometry distorted");
            Console.WriteLine("PASS: Native aspect and 4:3 pillarboxing");
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
        var full = ConvertNv12(16, 128, 128, options with { ColorRange = "전체 (Full)" });
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

    // The renderer consumes frames as they arrive, so take a fresh one as the comparison reference.
    private static Bitmap? DesktopReference(CapturePlayback capture, CaptureOptions options, Rectangle bounds)
    {
        byte[]? frame = null;
        for (var i = 0; i < 200 && frame is null; i++) { frame = capture.TakeFrame(); if (frame is null) Thread.Sleep(10); }
        if (frame is null) return null;
        try
        {
            var size = options.FrameSize;
            var image = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppRgb);
            var data = image.LockBits(new Rectangle(Point.Empty, size), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            try { Marshal.Copy(frame, 0, data.Scan0, size.Width * size.Height * 4); }
            finally { image.UnlockBits(data); }
            return image;
        }
        finally { CapturePlayback.ReturnFrame(frame); }
    }

    private static (int matched, int sampled) Compare(Bitmap screen, Bitmap reference, Rectangle target)
    {
        int matched = 0, sampled = 0;
        for (var y = 8; y < 100; y++)
            for (var x = 8; x < 100; x++)
            {
                var sx = target.X + target.Width * x / 100;
                var sy = target.Y + target.Height * y / 100;
                if (sx >= screen.Width || sy >= screen.Height) continue;
                var shown = screen.GetPixel(sx, sy);
                var want = reference.GetPixel(reference.Width * x / 100, reference.Height * y / 100);
                sampled++;
                // The frame is scaled on the GPU and the two grabs are moments apart, so allow drift.
                if (Math.Abs(shown.R - want.R) + Math.Abs(shown.G - want.G) + Math.Abs(shown.B - want.B) <= 60) matched++;
            }
        return (matched, sampled);
    }
}
