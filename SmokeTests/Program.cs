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
                Application.DoEvents();
                var hidden = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                Control Field(string name) => (Control)typeof(MainForm).GetField(name, hidden)!.GetValue(form)!;
                // A greyed grid has to say why, and a disabled control cannot hold a tooltip itself, so
                // the reason hangs on the grid behind the cells and on the caption beside them.
                var tips = (ToolTip)typeof(MainForm).GetField("anchorTips", hidden)!.GetValue(form)!;
                var cells = (RadioButton[])typeof(MainForm).GetField("anchorCells", hidden)!.GetValue(form)!;
                var chooser = (ComboBox)Field("mode");
                var soundTip = (ToolTip)typeof(MainForm).GetField("soundTip", hidden)!.GetValue(form)!;
                if (!tips.ShowAlways) throw new Exception("Tooltips would stay hidden unless the window is active");
                // Placement is asked of both inputs now, so it has to survive the switch between them.
                foreach (var (index, name) in new[] { (0, "capture"), (1, "video") })
                {
                    chooser.SelectedIndex = index;
                    Application.DoEvents();
                    using var preview = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(preview, new Rectangle(Point.Empty, preview.Size));
                    preview.Save($"artifacts/settings-{name}.png", ImageFormat.Png);
                    if (index == 0) preview.Save("artifacts/settings-preview.png", ImageFormat.Png);
                    if (!Field("layoutSettings").Visible) throw new Exception($"The placement settings are hidden in {name} mode");
                    if (Field("captureSettings").Visible != (index == 0)) throw new Exception($"The capture settings belong only to capture, not {name}");
                    var live = cells[0].Enabled;
                    var reason = tips.GetToolTip(Field("anchorGrid")) ?? "";
                    if (live && reason.Length > 0) throw new Exception("A usable grid should not explain itself away");
                    if (!live && reason.Length < 20) throw new Exception("A greyed grid must say why: " + reason);
                    if ((tips.GetToolTip(Field("anchorCaption")) ?? "").Length < 20) throw new Exception("The caption carries no explanation");
                    Console.WriteLine($"PASS: {name} settings rendered with placement on show (anchor grid {(live ? "live" : "greyed: " + reason)})");
                }
                // Sound follows the device: a card sends its own alongside the picture, a virtual camera
                // sends none, and a checkbox that cannot do anything has to say why.
                if (!soundTip.ShowAlways) throw new Exception("The sound explanation would stay hidden unless the window is active");
                var silence = (CheckBox)Field("mute");
                chooser.SelectedIndex = 1;
                Application.DoEvents();
                if (!silence.Enabled) throw new Exception("A video file always has sound to mute");
                chooser.SelectedIndex = 0;
                var box = (ComboBox)Field("devices");
                foreach (string device in box.Items)
                {
                    box.SelectedItem = device;
                    Application.DoEvents();
                    var why = soundTip.GetToolTip(silence) ?? "";
                    if (why.Length < 40) throw new Exception($"{device} leaves the mute box unexplained: " + why);
                    if (!silence.Enabled && !why.Contains("no sound")) throw new Exception($"{device} greys the box out without saying so: " + why);
                    Console.WriteLine($"PASS: {device} · mute {(silence.Enabled ? "live" : "greyed")}");
                }
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
                var mode = args.Contains("--squeeze") ? Placement.StretchFill : args.Contains("--pixels") ? Placement.Centred : Placement.FillCrop;
                var anchor = args.SkipWhile(a => a != "--anchor").Skip(1).FirstOrDefault(a => !a.StartsWith("--")) ?? "Center";
                var settings = new CaptureOptions(Resolution: res ?? CaptureOptions.Resolutions[0],
                    Aspect: custom is null ? chosen ?? Placement.Aspects[0] : Placement.Custom,
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
            // The interop that asks a device what its sound pin is offering. Worth running on its own
            // before anything depends on it: a vtable slot in the wrong place crashes the process
            // rather than returning an error.
            if (args.Contains("--rates"))
            {
                var name = args.SkipWhile(a => a != "--rates").Skip(1).Take(1).FirstOrDefault(a => !a.StartsWith("--"));
                foreach (var candidate in name is null ? CaptureDevices.Enumerate() : [name])
                {
                    var clock = Stopwatch.StartNew();
                    var rates = SoundFormats.Rates(candidate);
                    Console.WriteLine($"{candidate}: {(rates.Count == 0 ? "no answer" : string.Join(", ", rates))} ({clock.Elapsed.TotalMilliseconds:F1} ms)");
                }
                // Repeated, because a leak or a double free shows up on the tenth call rather than the first.
                var device = name ?? CaptureDevices.Enumerate()[0];
                var cold = Stopwatch.StartNew();
                for (var i = 0; i < 50; i++) SoundFormats.Rates(device);
                Console.WriteLine($"50 asks finding the pin each time: {cold.Elapsed.TotalMilliseconds / 50:F2} ms each");
                using (var held = new SoundFormats(device))
                {
                    held.Offered();
                    var warm = Stopwatch.StartNew();
                    for (var i = 0; i < 500; i++) held.Offered();
                    Console.WriteLine($"500 asks holding the pin: {warm.Elapsed.TotalMilliseconds / 500:F3} ms each");
                }
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Console.WriteLine("PASS: the device answered and the process is still standing");
                return 0;
            }
            // Sound and picture come out of one engine process, and the player reads that process's
            // stdout. A read there only returns when there is data or the writer is gone, so stopping
            // in the wrong order parks the UI thread on a read nothing will answer. This drives the
            // app's own playback path and times the stop.
            if (args.Contains("--sound") && !args.Contains("--capture"))
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                var device = args.SkipWhile(a => a != "--sound").Skip(1).Take(1).FirstOrDefault(a => !a.StartsWith("--"))
                    ?? CaptureDevices.Enumerate()[0];
                var audible = CaptureDevices.EnumerateAudio();
                Console.WriteLine($"DEVICES: {audible.Count} can send sound: {string.Join(", ", audible)}");
                var settings = new CaptureOptions(Audio: audible.Contains(device) ? device : "",
                    Follow: args.Contains("--eager") ? CaptureOptions.FollowEager : CaptureOptions.FollowRelaxed).Normalize();
                if (args.Contains("--follow")) Console.WriteLine($"FOLLOW: {settings.Follow}, asking every {settings.Watch.Every}ms, acting after {settings.Watch.Before}");
                if (!settings.HasSound) { Console.WriteLine($"SKIP: {device} sends no sound"); return 0; }
                using var dispatcher = new Control();
                _ = dispatcher.Handle;
                using var live = new Playback(dispatcher);
                var clock = Stopwatch.StartNew();
                live.Status += text => Console.WriteLine($"{clock.Elapsed.TotalSeconds,6:F1}s {text}");
                live.Start(new CaptureSource(device, 150, settings), Screen.PrimaryScreen!, mute: false);
                // A pin keeps its negotiated rate, so following a source that changes rate can only be
                // watched against a source that actually changes. The gap between the re-open notice
                // and the next Playing line is what the picture spends frozen.
                var watching = args.Contains("--follow") ? 600 : 5;
                Console.WriteLine($"{clock.Elapsed.TotalSeconds,6:F1}s watching for {watching}s" + (watching > 10 ? "; change the source rate whenever you like" : ""));
                var told = 0.0;
                while (clock.Elapsed.TotalSeconds < watching)
                {
                    Application.DoEvents();
                    Thread.Sleep(50);
                    // Printed as it goes, so a detector that never fires can be told apart from one
                    // that had nothing to fire at.
                    if (watching <= 10 || clock.Elapsed.TotalSeconds - told < 5) continue;
                    told = clock.Elapsed.TotalSeconds;
                    if (live.SoundArriving > 0)
                        Console.WriteLine($"{told,6:F1}s arriving {live.SoundArriving:F0} Hz" +
                            (live.SoundOffered > 0 ? $", device offering {live.SoundOffered} Hz" : ""));
                }
                // Muting must not disturb the picture, which is why the sound is captured either way.
                live.SetMute(true);
                for (var i = 0; i < 20; i++) { Application.DoEvents(); Thread.Sleep(50); }
                live.SetMute(false);
                for (var i = 0; i < 20; i++) { Application.DoEvents(); Thread.Sleep(50); }
                var stopping = Stopwatch.StartNew();
                live.Stop();
                Console.WriteLine($"STOP: playing with sound stopped in {stopping.ElapsedMilliseconds} ms");
                if (stopping.ElapsedMilliseconds > 3000) { Console.WriteLine("FAIL: stopping hung, most likely on the sound stream"); return 2; }
                Console.WriteLine("PASS: capture with sound plays, mutes live and stops cleanly");
                return 0;
            }
            // The loop is where a video wallpaper gives itself away: restarting the player throws the
            // video output away, and for as long as that takes there is a hole in the desktop with the
            // real wallpaper behind it. Only watching the screen across several loops can prove it gone.
            if (args.Contains("--video"))
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                var given = args.SkipWhile(a => a != "--video").Skip(1).Take(1).FirstOrDefault(a => !a.StartsWith("--"));
                // The fixture clip is one flat colour, which is what lets the picture be measured on
                // screen. A file the caller brought is played and watched, but not measured.
                var file = given ?? Clip(args.SkipWhile(a => a != "--seconds").Skip(1).FirstOrDefault() is { } d ? int.Parse(d) : 2);
                var chosen = args.SkipWhile(a => a != "--aspect").Skip(1).FirstOrDefault(a => !a.StartsWith("--"));
                var custom = args.SkipWhile(a => a != "--custom").Skip(1).FirstOrDefault(a => !a.StartsWith("--"));
                var mapping = args.Contains("--squeeze") ? Placement.StretchFill : args.Contains("--pixels") ? Placement.Centred
                    : args.Contains("--pad") ? Placement.FitPad : Placement.FillCrop;
                var anchor = args.SkipWhile(a => a != "--anchor").Skip(1).FirstOrDefault(a => !a.StartsWith("--")) ?? "Center";
                var layout = new Placement(custom is null ? chosen ?? Placement.AsCaptured : Placement.Custom,
                    custom ?? "1440x1080", mapping, anchor).Normalize();
                using var dispatcher = new Control();
                _ = dispatcher.Handle;
                using var playback = new Playback(dispatcher);
                // Off the UI thread: the parse completes on a VLC thread and would post its
                // continuation straight back to a message loop that is not running yet.
                var frame = Task.Run(() => VideoProbe.Measure(file)).GetAwaiter().GetResult();
                if (frame is null) { Console.WriteLine("FAIL: could not measure " + file); return 2; }
                var bounds = Screen.PrimaryScreen!.Bounds;
                var window = layout.Fit(frame.Value, bounds.Size);
                Console.WriteLine($"AREA: {file} {frame.Value.Width}x{frame.Value.Height} aspect={layout.Aspect} " +
                    $"custom={layout.CustomSize}/{layout.CustomMode}/{layout.Anchor} crop={layout.Crop(frame.Value)} -> {window}");
                window.Offset(bounds.Location);
                playback.Status += text => Console.WriteLine("STATUS: " + text);
                playback.Start(new VideoSource(file, layout, frame), Screen.PrimaryScreen!, true);
                var outcome = 2;
                var watch = new System.Windows.Forms.Timer { Interval = 2500 };
                watch.Tick += (_, _) =>
                {
                    watch.Stop();
                    object? shell = null;
                    try
                    {
                        if (!args.Contains("--stay"))
                        {
                            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
                            shell!.GetType().InvokeMember("MinimizeAll", System.Reflection.BindingFlags.InvokeMethod, null, shell, null);
                        }
                        for (var i = 0; i < 30; i++) { Application.DoEvents(); Thread.Sleep(50); }
                        var middle = new Point(window.X + window.Width / 2, window.Y + window.Height / 2);
                        // A window still standing over the picture would read as a hole that is not one.
                        // Only the points actually read matter: a picture that takes the whole monitor
                        // always has the taskbar somewhere over it, and that is not a reason to give up.
                        var covers = CoveringWindows();
                        Point[] edges = [new(window.X + 2, middle.Y), new(window.Right - 3, middle.Y),
                            new(middle.X, window.Y + 2), new(middle.X, window.Bottom - 3)];
                        if (covers.Any(cover => cover.Contains(middle)))
                        { Console.WriteLine("SKIP: something is covering the picture, cannot verify"); return; }
                        // A picture that takes the whole monitor always has the taskbar over an edge.
                        // That is no reason to abandon the loop test, only the geometry that needs them.
                        var edged = !edges.Any(point => covers.Any(cover => cover.Contains(point)));
                        var expected = Patch(middle);
                        Console.WriteLine($"COLOUR: the picture reads #{expected:X6} at its centre");
                        // Saved before anything is judged, so a failure leaves something to look at.
                        Rectangle drawn = Rectangle.Empty, covered = Rectangle.Empty;
                        using (var screen = new Bitmap(bounds.Width, bounds.Height))
                        {
                            using (var g = Graphics.FromImage(screen)) g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
                            screen.Save("artifacts/desktop-video.png", ImageFormat.Png);
                            if (given is null) { drawn = Drawn(screen, window, expected, false); covered = Drawn(screen, window, expected, true); }
                        }
                        // Measured rather than merely asserted, so a miss says by how much. The picture
                        // has to reach the window's own edges: anything short of them is either a bar
                        // that should have been cropped or a hole the wallpaper shows through.
                        if (given is null)
                            Console.WriteLine($"PICTURE: {drawn.Width} x {drawn.Height} at ({drawn.X}, {drawn.Y}), " +
                                $"painted {covered.Width} x {covered.Height}, " +
                                $"in a {window.Width} x {window.Height} window at ({window.X}, {window.Y})");
                        var duration = Seconds(file);
                        // Watched flat out rather than on a timer: the seam this is looking for is one
                        // frame wide, and a timer would step straight over it.
                        long samples = 0, holes = 0;
                        var clock = Stopwatch.StartNew();
                        var span = Math.Max(8.0, duration * 4 + 2);
                        while (clock.Elapsed.TotalSeconds < span)
                        {
                            samples++;
                            if (Far(Patch(middle), expected, 40)) holes++;
                            if (samples % 32 == 0) Application.DoEvents();
                        }
                        var seams = Math.Max(1, (int)(span / Math.Max(0.1, duration)));
                        Console.WriteLine($"LOOP: {samples} screen reads over {clock.Elapsed.TotalSeconds:F1}s " +
                            $"({seams} loops of a {duration:F1}s clip), {holes} of them showed something other than the picture");
                        if (samples < 200) { Console.WriteLine("SKIP: too few reads to judge the loop"); return; }
                        if (holes * 200 > samples) { Console.WriteLine("FAIL: the picture disappears, most likely at the loop"); return; }
                        // Whatever the mode, the window has to be painted corner to corner: anything it
                        // leaves is not black, it is a hole with the real wallpaper behind it.
                        if (given is null && !edged) Console.WriteLine("GEOMETRY: an edge is covered, so only the loop was judged");
                        if (given is null && edged && (covered.Width < window.Width - 2 || covered.Height < window.Height - 2))
                        {
                            Console.WriteLine($"FAIL: the window is painted only {covered.Width} x {covered.Height}, " +
                                "so the wallpaper shows through around the picture");
                            return;
                        }
                        // Where the mode crops, the bars are meant to be gone, so the picture itself has
                        // to reach the edges rather than merely something black.
                        if (given is null && edged && layout.Crop(frame.Value).Size != frame.Value
                            && (drawn.Width < window.Width - 2 || drawn.Height < window.Height - 2))
                        {
                            Console.WriteLine($"FAIL: the picture is {window.Width - drawn.Width} x {window.Height - drawn.Height} " +
                                "short of its window, so the bars were not cropped off");
                            return;
                        }
                        Console.WriteLine("PASS: the video is live on the desktop and survives its own loop (artifacts/desktop-video.png)");
                        outcome = 0;
                    }
                    catch (Exception ex) { Console.WriteLine("FAIL: " + ex.Message); }
                    finally
                    {
                        try { shell?.GetType().InvokeMember("UndoMinimizeALL", System.Reflection.BindingFlags.InvokeMethod, null, shell, null); } catch { }
                        playback.Stop();
                        Application.Exit();
                    }
                };
                watch.Start();
                Application.Run();
                return outcome;
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
                var name = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : devices[0];
                var captureOptions = new CaptureOptions(DynamicRange: args.Contains("--pq") ? CaptureOptions.DynamicRanges[1] : args.Contains("--hlg") ? CaptureOptions.DynamicRanges[2] : "SDR",
                    Audio: args.Contains("--sound") ? name : "");
                using var capture = new CapturePlayback(name, 150, captureOptions);
                string? failure = null;
                capture.Failed += message => { failure = message; Console.WriteLine(message); };
                capture.Start();
                // The whole sound path end to end: the engine's stdout, read as a stream by the player,
                // decoded, and written out by the file audio output so there is something to weigh.
                var recording = Path.GetFullPath("artifacts/capture-sound.wav");
                MediaPlayer? speaker = null;
                LibVLC? speakerEngine = null;
                if (capture.Sound is { } heard)
                {
                    if (File.Exists(recording)) File.Delete(recording);
                    // Its own engine, because the one above is deliberately deaf: --aout=dummy there
                    // would swallow the very thing this is trying to weigh.
                    // The output module is chosen for the instance, not for one media, so it goes here.
                    speakerEngine = new LibVLC("--no-video-title-show", "--vout=dummy", "--aout=afile",
                        "--audiofile-file=" + recording);
                    speakerEngine.Log += (_, e) => { if (e.Level >= LogLevel.Warning) Console.WriteLine($"VLC {e.Level}: {e.Module}: {e.Message}"); };
                    using var track = new Media(speakerEngine, new StreamMediaInput(heard), ":no-video", ":file-caching=150");
                    speaker = new MediaPlayer(speakerEngine);
                    speaker.Play(track);
                }
                Thread.Sleep(10000);
                if (speaker is not null)
                {
                    speaker.Stop();
                    speaker.Dispose();
                    speakerEngine?.Dispose();
                    var written = File.Exists(recording) ? new FileInfo(recording).Length : 0;
                    Console.WriteLine($"SOUND: {written / 1024} KB decoded out of the engine's stdout into {recording}");
                    if (written < 64 * 1024) { Console.WriteLine("FAIL: no sound came through"); return 2; }
                }
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
            // A 4:3 source pillarboxed into a 3840x2160 frame has exactly 480px of bar each side, and an
            // output resolution of 2732x2048 asks for the picture at exactly that size.
            var pillar = new CaptureOptions(Resolution: "3840x2160", Aspect: Placement.Custom, CustomSize: "2732x2048");
            if (pillar.Crop != new Rectangle(480, 0, 2880, 2160)) throw new Exception("Pillarbox not cropped off: " + pillar.Crop);
            if (pillar.Fit(screen).Size != new Size(2732, 2048)) throw new Exception("Fill did not land on the output size: " + pillar.Fit(screen));
            if (!pillar.ConversionFilter.StartsWith("crop=2880:2160:480:0,")) throw new Exception("Crop missing from the filter chain");
            // The whole point of an output resolution: the capture resolution must not change it.
            foreach (var capture in new[] { "1920x1080", "2560x1440", "3840x2160" })
            {
                var any = pillar with { Resolution = capture };
                if (any.Fit(screen).Size != new Size(2732, 2048)) throw new Exception($"{capture} gave {any.Fit(screen).Size}, not the output size");
                if (Math.Abs((double)any.Crop.Width / any.Crop.Height - 2732d / 2048) > 0.01) throw new Exception($"{capture} cut the wrong shape");
            }
            // The anchor moves the picture on the monitor; the cut itself always stays centred, because
            // that is where the bars are.
            foreach (var (anchor, expected) in new[]
            {
                ("Top left", new Point(0, 0)), ("Left", new Point(0, 56)), ("Right", new Point(1108, 56)),
                ("Bottom right", new Point(1108, 112)), ("Center", new Point(554, 56)), ("Top", new Point(554, 0)),
            })
            {
                var placed = pillar with { Anchor = anchor };
                if (placed.Fit(screen).Location != expected) throw new Exception($"Anchor {anchor} put the picture at {placed.Fit(screen)}");
                if (placed.Crop != new Rectangle(480, 0, 2880, 2160)) throw new Exception($"Anchor {anchor} moved the crop");
            }
            // An output larger than the monitor still has to fit on it, shape intact.
            var huge = pillar with { CustomSize = "7680x5760" };
            if (huge.Fit(screen) != new Rectangle(480, 0, 2880, 2160)) throw new Exception("Oversized output not bounded: " + huge.Fit(screen));
            // Centre keeps one source pixel per screen pixel and never scales up to the output size.
            var exact = pillar with { CustomMode = Placement.Centred };
            if (exact.Crop != new Rectangle(554, 56, 2732, 2048)) throw new Exception("Centre cut " + exact.Crop);
            if (exact.Fit(screen).Size != new Size(2732, 2048)) throw new Exception("Centre did not stay 1:1");
            if ((exact with { Resolution = "1920x1080" }).Fit(screen).Size != new Size(1920, 1080))
                throw new Exception("Centre should not invent pixels the capture does not have");
            // Fit keeps the whole frame and shrinks it until it sits inside the output.
            var padded = pillar with { CustomMode = Placement.FitPad };
            if (padded.Crop != new Rectangle(0, 0, 3840, 2160)) throw new Exception("Fit should not crop");
            if (padded.Fit(screen).Size != new Size(2732, 1537)) throw new Exception("Fit landed on " + padded.Fit(screen).Size);
            // Stretching an anamorphic frame crops nothing and fills the output exactly, distortion included.
            var squeezed = pillar with { CustomMode = Placement.StretchFill };
            if (squeezed.Crop != new Rectangle(0, 0, 3840, 2160)) throw new Exception("Stretch should not crop");
            if (squeezed.ConversionFilter.Contains("crop=")) throw new Exception("Stretch should not add a crop filter");
            if (squeezed.Fit(screen).Size != new Size(2732, 2048)) throw new Exception("Stretch did not fill the output");
            // A video file is placed by exactly the same arithmetic, with the shape the file turned out
            // to be standing in for the capture resolution. The two must never disagree.
            var clip = new Size(1920, 1080);
            var shared = new Placement(Placement.Custom, "1440x1080", Placement.FillCrop, "Center");
            if (shared.Crop(clip) != new Rectangle(240, 0, 1440, 1080)) throw new Exception("Pillarbox not cropped off a video: " + shared.Crop(clip));
            if (shared.Fit(clip, screen) != new Rectangle(1200, 540, 1440, 1080)) throw new Exception("Video placed at " + shared.Fit(clip, screen));
            var twin = new CaptureOptions(Resolution: "1920x1080", Aspect: Placement.Custom, CustomSize: "1440x1080");
            if (twin.Crop != shared.Crop(clip) || twin.Fit(screen) != shared.Fit(clip, screen))
                throw new Exception("A video and a capture of the same shape were placed differently");
            if ((shared with { Anchor = "Bottom left" }).Fit(clip, screen).Location != new Point(0, 1080))
                throw new Exception("Anchors do not reach the video path");
            // Sound rides the same input as the picture, because a card will not hand it over as a
            // device of its own, and it leaves on stdout, which the picture is far too big for.
            var silent = new CaptureOptions().Normalize();
            var loud = (new CaptureOptions() with { Audio = "Live Gamer BOLT" }).Normalize();
            var quietRun = silent.CreateStartInfo("Some Card", 150, "pipe");
            var loudRun = loud.CreateStartInfo("Some Card", 150, "pipe");
            var quietArgs = string.Join(" ", quietRun.ArgumentList);
            var loudArgs = string.Join(" ", loudRun.ArgumentList);
            if (silent.HasSound || quietArgs.Contains("audio=") || quietArgs.Contains("pipe:1") || quietRun.RedirectStandardOutput)
                throw new Exception("A silent capture asked for sound anyway: " + quietArgs);
            if (!loudArgs.Contains("video=Some Card:audio=Live Gamer BOLT")) throw new Exception("Sound was not asked for on the picture's own input: " + loudArgs);
            if (!loudArgs.Contains("-map 0:a") || !loudArgs.Contains("-f wav pipe:1")) throw new Exception("Sound has nowhere to leave: " + loudArgs);
            // Two channels asked for, and deliberately no rate: the device puts what it is receiving at
            // the head of its format list, so saying nothing is how its own answer gets used. Saying
            // nothing about the channels too would let a 7.1 card hand two channels over as eight.
            if (!loudArgs.Contains("-channels 2")) throw new Exception("The sound layout was left to whatever the device listed first: " + loudArgs);
            if (loudArgs.Contains("-sample_rate")) throw new Exception("A rate was forced on the device instead of taking its own: " + loudArgs);
            if (!loudArgs.Contains("-ac 2")) throw new Exception("Sound could reach the player in some layout it will not be played in: " + loudArgs);
            if (quietArgs.Contains("-channels")) throw new Exception("A silent capture negotiated a sound format: " + quietArgs);
            // Following the source costs an ask into the device, so it is a choice, and Off has to mean off.
            if (silent.Following) throw new Exception("A silent capture was going to watch for rate changes");
            if (!loud.Following) throw new Exception("A capture with sound was not going to follow the source");
            if ((loud with { Follow = CaptureOptions.FollowOff }).Normalize().Following) throw new Exception("Off did not turn it off");
            var relaxed = loud.Watch;
            var eager = (loud with { Follow = CaptureOptions.FollowEager }).Normalize().Watch;
            if (relaxed.Every <= eager.Every) throw new Exception("Eager is not more eager than relaxed");
            // Both settle in well under the second and a half a re-open itself takes.
            if (eager.Every * eager.Before > 600 || relaxed.Every * relaxed.Before > 5000)
                throw new Exception($"Settling takes {eager.Every * eager.Before}ms eager, {relaxed.Every * relaxed.Before}ms relaxed");
            if (!loudRun.RedirectStandardOutput) throw new Exception("Nothing is listening on the engine's stdout");
            if (!loudArgs.Contains("-map 0:v")) throw new Exception("The picture lost its own mapping once sound was added");
            Console.WriteLine("PASS: output resolution, fill/fit/centre/stretch, anchors, shared by video and capture, sound on one input");
            TestColors();
            using var control = new Control();
            using var idle = new Playback(control);
            idle.Stop(); idle.Stop();
            Console.WriteLine("PASS: Idle playback cleanup is repeatable");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    // A known clip beats whatever happens to be lying around: 240px of black bar either side of a
    // solid colour, which is the pillarbox this app exists to remove, and short enough to loop often.
    private static string Clip(int seconds)
    {
        var file = Path.GetFullPath($"artifacts/loop-clip-{seconds}s.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        if (File.Exists(file)) return file;
        var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "capture", "ffmpeg.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-y", "-v", "error", "-f", "lavfi", "-i", $"color=c=0x2060F0:s=1440x1080:r=30:d={seconds}",
            "-vf", "pad=1920:1080:240:0:black", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", file })
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var trouble = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new Exception("Could not build a test clip: " + trouble);
        return file;
    }

    private static double Seconds(string file)
    {
        var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "capture", "ffmpeg.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-hide_banner", "-i", file }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var text = process.StandardError.ReadToEnd();
        process.WaitForExit();
        var match = System.Text.RegularExpressions.Regex.Match(text, @"Duration: (\d+):(\d+):(\d+\.\d+)");
        return match.Success
            ? int.Parse(match.Groups[1].Value) * 3600 + int.Parse(match.Groups[2].Value) * 60 + double.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture)
            : 2;
    }

    // An 8x8 mean rather than one pixel, so dithering and scaling do not read as a hole.
    private static readonly Bitmap Swatch = new(8, 8);
    private static readonly Graphics Lens = Graphics.FromImage(Swatch);
    private static int Patch(Point at)
    {
        Lens.CopyFromScreen(new Point(at.X - 4, at.Y - 4), Point.Empty, new Size(8, 8));
        int r = 0, g = 0, b = 0;
        for (var y = 0; y < 8; y++)
            for (var x = 0; x < 8; x++)
            { var pixel = Swatch.GetPixel(x, y); r += pixel.R; g += pixel.G; b += pixel.B; }
        return (r / 64 << 16) | (g / 64 << 8) | b / 64;
    }

    // How far the picture reaches inside the window it was given, walked in from all four edges.
    // With bars counted it measures what is painted at all; without them, the picture proper.
    private static Rectangle Drawn(Bitmap screen, Rectangle window, int colour, bool countBars)
    {
        int At(int x, int y) { var pixel = screen.GetPixel(x, y); return (pixel.R << 16) | (pixel.G << 8) | pixel.B; }
        bool Bar(int pixel) => countBars && (pixel >> 16 & 255) + (pixel >> 8 & 255) + (pixel & 255) < 60;
        bool Blank(int x, int y) { var pixel = At(x, y); return Far(pixel, colour, 60) && !Bar(pixel); }
        int middleY = window.Y + window.Height / 2, middleX = window.X + window.Width / 2;
        int left = window.X, right = window.Right - 1, top = window.Y, bottom = window.Bottom - 1;
        while (left < right && Blank(left, middleY)) left++;
        while (right > left && Blank(right, middleY)) right--;
        while (top < bottom && Blank(middleX, top)) top++;
        while (bottom > top && Blank(middleX, bottom)) bottom--;
        return Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
    }

    private static bool Far(int one, int other, int tolerance) =>
        Math.Abs((one >> 16 & 255) - (other >> 16 & 255)) + Math.Abs((one >> 8 & 255) - (other >> 8 & 255))
            + Math.Abs((one & 255) - (other & 255)) > tolerance;

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
