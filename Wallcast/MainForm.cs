using System.Text.Json;

namespace Wallcast;

internal sealed class MainForm : Form
{
    // Capture leads: it is what the app is for, and it is the default on a fresh install.
    private static readonly string[] Modes = ["Capture card / virtual camera", "Video file"];
    private readonly ComboBox mode = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private bool Capturing => mode.SelectedIndex == 0;
    private readonly TextBox path = new() { ReadOnly = true, PlaceholderText = "Choose a video file" };
    private readonly ComboBox devices = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox monitors = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly NumericUpDown cache = new() { Minimum = 50, Maximum = 2000, Increment = 50, Value = 150 };
    // One control for the whole of sound. Muting while still following a source that changes rate was
    // two settings saying different things about the same intention.
    private readonly ComboBox sound = Choice(CaptureOptions.Sounds);
    // A disabled control gets no mouse messages, so the reason it is disabled has to hang on a parent.
    private readonly FlowLayoutPanel soundRow = Row();
    private readonly ToolTip soundTip = new() { ShowAlways = true, AutoPopDelay = 15000, InitialDelay = 350 };
    // Which devices the engine says can hand over sound. A capture card carries its own on a pin of the
    // video device; a virtual camera has none, and asking it for sound would cost the picture too.
    private string[] audible = [];
    private readonly Label status = new() { Text = "Choose an input and apply it.", AutoSize = true, MaximumSize = new Size(490, 0) };
    private readonly FlowLayoutPanel fileRow = Row();
    private readonly FlowLayoutPanel captureRow = Row();
    private readonly FlowLayoutPanel cacheRow = Row();
    private readonly TableLayoutPanel captureSettings = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Margin = new Padding(0, 8, 0, 0) };
    // Placement is asked of both inputs, so it lives in its own table that neither mode hides.
    private readonly TableLayoutPanel layoutSettings = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Margin = new Padding(0, 8, 0, 0) };
    private readonly ComboBox format = Choice(CaptureOptions.Formats);
    private readonly ComboBox resolution = Choice(CaptureOptions.Resolutions);
    private readonly ComboBox fps = Choice(CaptureOptions.FrameRates);
    private readonly ComboBox colorSpace = Choice(CaptureOptions.ColorSpaces);
    private readonly ComboBox colorRange = Choice(CaptureOptions.ColorRanges);
    private readonly ComboBox aspect = Choice(Placement.Aspects);
    private readonly TextBox customSize = new() { Width = 325, Margin = new Padding(3, 4, 3, 4), PlaceholderText = "2732x2048" };
    private readonly ComboBox customMode = Choice(Placement.CustomModes);
    // A canvas-size anchor: nine cells, the arrows pointing where on the monitor the picture goes.
    private static readonly string[] AnchorGlyphs = ["↖", "↑", "↗", "←", "●", "→", "↙", "↓", "↘"];
    private readonly RadioButton[] anchorCells = new RadioButton[Placement.Anchors.Length];
    private readonly TableLayoutPanel anchorGrid = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3, RowCount = 3, Margin = new Padding(3, 4, 3, 4) };
    // Without ShowAlways a tooltip stays hidden unless the window is active, and a disabled control
    // never gets the mouse itself, so the reason is hung on the grid and the caption behind it.
    private readonly ToolTip anchorTips = new() { ShowAlways = true, AutoPopDelay = 15000, InitialDelay = 350 };
    private bool anchorsActive = true;
    private Label? anchorCaption;
    // A video's shape comes from the file, not from a combo box, so it has to be measured first.
    private Size? videoFrame;
    private string probing = "";
    // A size box that sometimes reads as a ratio and sometimes as pixels needs to show its work.
    private readonly Label geometry = new() { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(490, 0), Margin = new Padding(0, 8, 0, 0) };
    private readonly ComboBox dynamicRange = Choice(CaptureOptions.DynamicRanges);
    private readonly ComboBox hdrPeak = Choice(CaptureOptions.HdrPeaks);

    private readonly NotifyIcon tray;
    private readonly System.Windows.Forms.Timer watchdog = new() { Interval = 2000 };
    private Playback? playback;
    private bool exiting;
    private Screen[] screens = [];
    private static readonly string SettingsPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wallcast", "settings.json");

    public MainForm()
    {
        SuspendLayout();
        AutoScaleMode = AutoScaleMode.None;
        Text = $"Wallcast {Release} · Live wallpaper";
        AutoScaleDimensions = new SizeF(96, 96);
        ClientSize = new Size(580, 905);
        MinimumSize = new Size(580, 660);
        Font = new Font("Segoe UI", 10);
        BackColor = Color.FromArgb(246, 247, 250);
        var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(28), AutoScroll = true };
        Controls.Add(body);
        body.Controls.Add(new Label { Text = "Wallcast", Font = new Font("Segoe UI", 28, FontStyle.Bold), AutoSize = true });
        body.Controls.Add(new Label { Text = "Any screen, as your wallpaper.", AutoSize = true, Margin = new Padding(0, 0, 0, 22) });
        body.Controls.Add(Caption("Input source"));
        mode.Items.AddRange(Modes);
        mode.Width = 490;
        mode.SelectedIndex = 0;
        body.Controls.Add(mode);
        path.Width = 365;
        fileRow.Controls.Add(path);
        fileRow.Controls.Add(Button("Browse", PickFile));
        body.Controls.Add(fileRow);
        devices.Width = 365;
        captureRow.Controls.Add(devices);
        captureRow.Controls.Add(Button("Refresh", (_, _) => RefreshDevices()));
        body.Controls.Add(captureRow);
        AddCaptureSetting("Pixel format", format);
        AddCaptureSetting("Resolution", resolution);
        AddCaptureSetting("Frame rate", fps);
        AddCaptureSetting("Color space", colorSpace);
        AddCaptureSetting("Color range", colorRange);
        AddCaptureSetting("Input HDR", dynamicRange);
        AddCaptureSetting("HDR peak (nits)", hdrPeak);
        AddLayoutSetting("Display aspect", aspect);
        AddLayoutSetting("Output size (W x H)", customSize);
        AddLayoutSetting("Output mapping", customMode);
        for (var cell = 0; cell < anchorCells.Length; cell++)
        {
            var button = new RadioButton
            {
                // AutoCheck would hand the selection to whichever cell the form happens to focus first.
                AutoCheck = false,
                // Flat, because the themed button keeps its blue accent even when disabled, which is
                // exactly the state that has to be unmistakable here.
                Appearance = Appearance.Button, FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false,
                Text = AnchorGlyphs[cell], Tag = Placement.Anchors[cell],
                Width = 30, Height = 28, Margin = new Padding(1), TextAlign = ContentAlignment.MiddleCenter,
                Checked = Placement.Anchors[cell] == "Center"
            };
            button.Click += (sender, _) =>
            {
                foreach (var other in anchorCells) other.Checked = ReferenceEquals(other, sender);
                PaintAnchors();
            };
            anchorTips.SetToolTip(button, Placement.Anchors[cell]);
            anchorCells[cell] = button;
            anchorGrid.Controls.Add(button, cell % 3, cell / 3);
        }
        anchorCaption = AddLayoutSetting("Screen position", anchorGrid);
        aspect.SelectedIndexChanged += (_, _) => UpdateAspectControls();
        customMode.SelectedIndexChanged += (_, _) => UpdateAspectControls();
        // Room depends on the capture shape and the monitor too, not just on the aspect choice.
        resolution.SelectedIndexChanged += (_, _) => UpdateAspectControls();
        customSize.TextChanged += (_, _) => UpdateAspectControls();
        UpdateAspectControls();
        dynamicRange.SelectedIndexChanged += (_, _) => UpdateHdrControls();
        UpdateHdrControls();
        body.Controls.Add(captureSettings);
        body.Controls.Add(Caption("Placement"));
        body.Controls.Add(layoutSettings);
        body.Controls.Add(geometry);
        cacheRow.Controls.Add(new Label { Text = "Capture buffer (ms)", AutoSize = true, Padding = new Padding(0, 5, 10, 0) });
        cacheRow.Controls.Add(cache);
        body.Controls.Add(cacheRow);
        body.Controls.Add(Caption("Output monitor"));
        monitors.Width = 490;
        body.Controls.Add(monitors);
        soundRow.Margin = new Padding(0, 14, 0, 10);
        soundRow.Controls.Add(new Label { Text = "Sound", AutoSize = true, Padding = new Padding(0, 5, 14, 0) });
        sound.Width = 380;
        soundRow.Controls.Add(sound);
        body.Controls.Add(soundRow);
        body.Controls.Add(new Label { Text = "Keeps the source shape unless told otherwise · video loops without a seam · sound starts off and is one setting", AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(490, 0) });
        var actions = Row();
        actions.Margin = new Padding(0, 20, 0, 12);
        actions.Controls.Add(Button("Apply to desktop", Apply));
        actions.Controls.Add(Button("Stop", (_, _) => Stop()));
        actions.Controls.Add(Button("Hide to tray", (_, _) => Hide()));
        body.Controls.Add(actions);
        body.Controls.Add(status);
        mode.SelectedIndexChanged += (_, _) => UpdateMode();
        path.TextChanged += (_, _) => ProbeVideo();
        monitors.SelectedIndexChanged += (_, _) => UpdateAspectControls();
        devices.SelectedIndexChanged += (_, _) => UpdateSoundControls();
        // A file can be silenced where it stands; a device has to be re-opened to stop asking it for
        // sound at all, which is what Apply is for, and every other capture setting works the same way.
        sound.SelectedIndexChanged += (_, _) => { if (!Capturing) playback?.SetMute(sound.Text == CaptureOptions.SoundOff); };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Wallcast", null, (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); });
        menu.Items.Add("Stop wallpaper", null, (_, _) => Stop());
        menu.Items.Add("Exit", null, (_, _) => { exiting = true; Close(); });
        Icon = LoadIcon(32);
        tray = new NotifyIcon { Icon = LoadIcon(16), Text = $"Wallcast {Release} · Live wallpaper", Visible = true, ContextMenuStrip = menu };
        tray.DoubleClick += (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); };
        Shown += (_, _) =>
        {
            var area = Screen.FromControl(this).WorkingArea;
            Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
            Top = Math.Max(area.Top, Math.Min(Top, area.Bottom - Height));
            Initialize();
        };
        FormClosing += (_, e) =>
        {
            if (!exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        };
        FormClosed += (_, _) => { watchdog.Dispose(); playback?.Dispose(); tray.Dispose(); };
        watchdog.Tick += (_, _) =>
        {
            var current = Screen.AllScreens;
            if (playback?.IsDesktopAlive == false || !current.Select(s => (s.DeviceName, s.Bounds)).SequenceEqual(screens.Select(s => (s.DeviceName, s.Bounds))))
            {
                Stop(); RefreshMonitors();
                status.Text = "The desktop or monitor layout changed. Apply again.";
            }
        };
        UpdateMode();
        ScaleGeometry(this, DeviceDpi / 96f);
        ResumeLayout(true);
    }

    // Build every row at the same DPI, including rows initially hidden by the source selector.
    private static void ScaleGeometry(Control control, float factor)
    {
        int S(int value) => (int)Math.Round(value * factor);
        Padding P(Padding value) => new(S(value.Left), S(value.Top), S(value.Right), S(value.Bottom));
        control.SuspendLayout();
        control.Padding = P(control.Padding); control.Margin = P(control.Margin);
        if (!control.AutoSize && control.Dock == DockStyle.None)
            control.Size = new Size(S(control.Width), control is ComboBox or TextBox or NumericUpDown ? control.Height : S(control.Height));
        control.MinimumSize = new Size(S(control.MinimumSize.Width), S(control.MinimumSize.Height));
        control.MaximumSize = new Size(S(control.MaximumSize.Width), S(control.MaximumSize.Height));
        foreach (Control child in control.Controls) ScaleGeometry(child, factor);
        control.ResumeLayout(true);
    }

    private void Initialize()
    {
        RefreshMonitors(); RefreshDevices(); LoadSettings(); UpdateMode();
        try { playback = new Playback(this); playback.Status += text => status.Text = text; watchdog.Start(); }
        catch (Exception ex) { status.Text = "Playback engine failed to start: " + ex.Message; }
    }

    private void RefreshMonitors()
    {
        var old = monitors.SelectedIndex;
        screens = Screen.AllScreens;
        monitors.Items.Clear();
        foreach (var screen in screens) monitors.Items.Add($"{screen.DeviceName} · {screen.Bounds.Width} × {screen.Bounds.Height}{(screen.Primary ? " · primary" : "")}");
        if (screens.Length > 0) monitors.SelectedIndex = Math.Clamp(old, 0, screens.Length - 1);
    }

    private void RefreshDevices()
    {
        var old = devices.SelectedItem as string;
        devices.Items.Clear();
        try
        {
            // Asking the engine costs a short run, so it happens here rather than on every repaint.
            try { audible = CaptureDevices.EnumerateAudio().ToArray(); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException) { audible = []; }
            devices.Items.AddRange(CaptureDevices.Enumerate().Cast<object>().ToArray());
            if (old is not null && devices.Items.Contains(old)) devices.SelectedItem = old;
            else if (devices.Items.Count > 0) devices.SelectedIndex = 0;
            else status.Text = "No capture devices. Connect one and refresh.";
        }
        catch (Exception ex) { status.Text = "Device search failed: " + ex.Message; }
    }

    private void PickFile(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog { Title = "Choose a wallpaper video", Filter = "Video|*.mp4;*.mkv;*.mov;*.webm;*.avi;*.m4v;*.wmv;*.ts|All files|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(this) == DialogResult.OK) path.Text = dialog.FileName;
    }

    private void Apply(object? sender, EventArgs e)
    {
        try
        {
            if (playback is null) throw new InvalidOperationException("The playback engine is unavailable. Restart the app.");
            if (monitors.SelectedIndex < 0) throw new InvalidOperationException("Select a monitor.");
            IWallpaperSource input;
            if (Capturing)
            {
                if (devices.SelectedItem is not string name) throw new InvalidOperationException("Select a capture device.");
                input = new CaptureSource(name, (int)cache.Value, SelectedCaptureOptions());
            }
            else
            {
                if (!File.Exists(path.Text)) throw new InvalidOperationException("Select a video file.");
                input = new VideoSource(path.Text, SelectedPlacement(), videoFrame);
            }
            status.Text = "Opening the input…";
            playback.Start(input, screens[monitors.SelectedIndex], sound.Text == CaptureOptions.SoundOff);
            SaveSettings();
        }
        catch (Exception ex) { status.Text = ex.Message; }
    }

    private void Stop() { playback?.Stop(); status.Text = "Stopped · your original wallpaper is back."; }
    private void UpdateMode()
    {
        var parent = captureRow.Parent;
        parent?.SuspendLayout();
        fileRow.Visible = !Capturing;
        captureRow.Visible = cacheRow.Visible = captureSettings.Visible = Capturing;

        if (parent is not null) parent.Controls.SetChildIndex(captureRow, parent.Controls.GetChildIndex(mode) + 1);
        parent?.ResumeLayout(true);
        // The frame a placement applies to changes with the mode, so the readout has to be redone.
        UpdateAspectControls();
        UpdateSoundControls();
    }
    // Sound follows the input: a video file always has some, a capture device only if it sends any.
    private bool DeviceHasSound => devices.SelectedItem is string name && audible.Contains(name);
    private void UpdateSoundControls()
    {
        var available = !Capturing || DeviceHasSound;
        sound.Enabled = available;
        // The two following entries are about a device changing its mind mid-stream, which a file on
        // disk never does, so in video mode they are not offered at all.
        var offered = Capturing ? CaptureOptions.Sounds : CaptureOptions.FileSounds;
        if (!sound.Items.Cast<string>().SequenceEqual(offered))
        {
            var chosen = sound.Text;
            sound.Items.Clear();
            sound.Items.AddRange(offered.Cast<object>().ToArray());
            sound.SelectedIndex = Math.Max(0, Array.IndexOf(offered, chosen));
        }
        // Laid out as the list it is. A tooltip is drawn as one line unless the text says otherwise,
        // and one line of this runs off the edge of the screen.
        var reason = !available
            ? Wrap("This device sends a picture and no sound, so there is nothing to turn on. A virtual "
                + "camera has no sound of its own: route its audio through a virtual audio device and "
                + "capture that instead.")
            : !Capturing
                ? Wrap("Whether the video file is heard. It takes effect where it stands, without applying again.")
            : string.Join(Environment.NewLine,
                "Off — the device is asked for no sound at all. Nothing is captured and nothing is spent.",
                "On — the sound the device is sending when you press Apply. It re-opens itself if the",
                "    sound starts arriving at a rate that plainly disagrees, which costs nothing to",
                "    notice; a card that quietly resamples instead hides that, and this will not catch it.",
                "On, following the source — also asks the device what it is receiving, which catches the",
                "    hidden case too, within about four seconds. Under half a percent of a core.",
                "On, following closely — the same within about a second, for a source that changes",
                "    rate track by track. Under two percent.",
                "",
                "Re-opening freezes the picture for about a second and a half; it does not go black.",
                "Takes effect on Apply, like every other capture setting.");
        soundTip.SetToolTip(sound, reason);
        soundTip.SetToolTip(soundRow, reason);
    }

    private static ComboBox Choice(string[] values)
    {
        var choice = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 325, Margin = new Padding(3, 4, 3, 4) };
        choice.Items.AddRange(values); choice.SelectedIndex = 0;
        return choice;
    }
    private Label AddCaptureSetting(string text, Control control) => AddSetting(captureSettings, text, control);
    private Label AddLayoutSetting(string text, Control control) => AddSetting(layoutSettings, text, control);
    private static Label AddSetting(TableLayoutPanel table, string text, Control control)
    {
        var row = table.RowCount++;
        var caption = new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 22, 0) };
        table.Controls.Add(caption, 0, row);
        table.Controls.Add(control, 1, row);
        return caption;
    }

    // What the placement will be applied to: the capture resolution the user picked, or the shape the
    // chosen video turned out to have. Null means there is nothing to place yet.
    private Size? SourceFrame => Capturing ? SelectedCaptureOptions().Normalize().FrameSize : videoFrame;

    // Measuring touches the disk, so it happens as the file is chosen rather than as it is applied.
    private async void ProbeVideo()
    {
        var file = path.Text;
        probing = file;
        videoFrame = null;
        UpdateAspectControls();
        if (string.IsNullOrWhiteSpace(file)) return;
        Size? measured = null;
        try { measured = await VideoProbe.Measure(file); }
        catch (Exception ex) { status.Text = "Could not read the video: " + ex.Message; }
        if (IsDisposed || probing != file) return;
        videoFrame = measured;
        if (measured is null && File.Exists(file)) status.Text = "That file carries no video track this build can read.";
        UpdateAspectControls();
    }
    // The custom size only means anything for the Custom entry, so it stays out of the way otherwise.
    private void UpdateAspectControls()
    {
        var layout = SelectedPlacement();
        customSize.Enabled = customMode.Enabled = layout.Aspect == Placement.Custom;
        // The anchor can only do something where the picture leaves room on the monitor. A 16:9 source
        // filling a 16:9 screen leaves none, and a grid that looks live but moves nothing reads as a bug.
        string? blocked = null;
        if (monitors.SelectedIndex < 0 || monitors.SelectedIndex >= screens.Length) blocked = "Choose an output monitor first.";
        else if (SourceFrame is not { } frame) blocked = "Choose a video file first: where the picture can move depends on the shape it turns out to be.";
        else
        {
            var monitor = screens[monitors.SelectedIndex].Bounds.Size;
            var placed = layout.Fit(frame, monitor);
            if (placed.Width >= monitor.Width && placed.Height >= monitor.Height)
                blocked = layout.Aspect == Placement.Stretch
                    ? $"Stretch to screen fills the whole {monitor.Width} x {monitor.Height} monitor, so there is nowhere to move the picture."
                    : $"The picture already covers the whole {monitor.Width} x {monitor.Height} monitor, so there is nowhere to move it. Give it an output resolution smaller than the monitor to free up room.";
        }
        anchorsActive = blocked is null;
        // A disabled control gets no mouse messages, so the reason has to live on the grid behind them.
        anchorTips.SetToolTip(anchorGrid, blocked is null ? string.Empty : Wrap(blocked));
        UpdateGeometry();
        if (anchorCaption is not null) anchorTips.SetToolTip(anchorCaption, Wrap(blocked ?? "Where the picture sits on the monitor."));
        PaintAnchors();
    }
    private void UpdateHdrControls()
    {
        var hdr = dynamicRange.SelectedIndex > 0;
        hdrPeak.Enabled = hdr;
        colorSpace.Enabled = !hdr;
        if (hdr) colorSpace.SelectedItem = "Rec.2020";
        else if (colorSpace.Text == "Rec.2020") colorSpace.SelectedItem = "Rec.709";
    }
    // The sound comes off the same device as the picture, so there is nothing separate to choose. It is
    // taken whenever the device offers it and silenced by Mute, which keeps the toggle instant: asking
    // the engine for it only on demand would mean restarting the capture every time it is clicked.
    private CaptureOptions SelectedCaptureOptions() => new(format.Text, resolution.Text, fps.Text, colorSpace.Text, colorRange.Text, aspect.Text, dynamicRange.Text, hdrPeak.Text, customSize.Text, customMode.Text, SelectedAnchor,
        Capturing && DeviceHasSound ? (string)devices.SelectedItem! : "", sound.Text);
    private Placement SelectedPlacement() => new Placement(aspect.Text, customSize.Text, customMode.Text, SelectedAnchor).Normalize();
    // The .ico carries a drawing per size, so ask for the one that fits rather than scaling one down.
    private static Icon LoadIcon(int size)
    {
        using var stream = typeof(MainForm).Assembly.GetManifestResourceStream("Wallcast.Wallcast.ico");
        return stream is null ? SystemIcons.Application : new Icon(stream, new Size(size, size));
    }
    // Worth a glance in the title bar: the first thing a bug report needs is which build it came from.
    private static string Release =>
        typeof(MainForm).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0]
        ?? typeof(MainForm).Assembly.GetName().Version?.ToString(3) ?? "";

    private string SelectedAnchor => anchorCells.FirstOrDefault(cell => cell.Checked)?.Tag as string ?? "Center";

    // Spells out what the current numbers actually do, because "2732x2048" and "1024x768" mean the
    // same thing in a shape mode and quite different things in a pixel one.
    private void UpdateGeometry()
    {
        if (monitors.SelectedIndex < 0 || monitors.SelectedIndex >= screens.Length) { geometry.Text = ""; return; }
        if (SourceFrame is not { } frame) { geometry.Text = "Choose a video file to see where it will be drawn."; return; }
        var monitor = screens[monitors.SelectedIndex].Bounds.Size;
        var layout = SelectedPlacement();
        var cut = layout.Crop(frame);
        var placed = layout.Fit(frame, monitor);
        var taken = cut.Size == frame
            ? $"Uses the whole {frame.Width} × {frame.Height} frame"
            : $"Cuts {cut.Width} × {cut.Height} out of {frame.Width} × {frame.Height} at ({cut.X}, {cut.Y})";
        var covers = placed.Width >= monitor.Width && placed.Height >= monitor.Height;
        geometry.Text = $"{taken} · drawn {placed.Width} × {placed.Height} at ({placed.X}, {placed.Y}) · " +
            (covers ? "covers the monitor" : "your wallpaper shows around it");
    }

    // Greyed has to read as greyed at a glance, including on the cell that happens to be chosen.
    private void PaintAnchors()
    {
        foreach (var cell in anchorCells)
        {
            cell.Enabled = anchorsActive;
            var chosen = cell.Checked;
            cell.BackColor = !anchorsActive ? SystemColors.Control
                : chosen ? Color.FromArgb(0, 103, 192) : SystemColors.Window;
            cell.ForeColor = !anchorsActive ? SystemColors.GrayText
                : chosen ? Color.White : SystemColors.ControlText;
            cell.FlatAppearance.BorderColor = !anchorsActive ? SystemColors.ControlDark
                : chosen ? Color.FromArgb(0, 78, 145) : SystemColors.ControlDark;
        }
    }
    // WinForms draws a tooltip on one line however long it is, so the breaks have to be put in.
    private static string Wrap(string text, int width = 84)
    {
        var lines = new List<string>();
        var line = new System.Text.StringBuilder();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width) { lines.Add(line.ToString()); line.Clear(); }
            if (line.Length > 0) line.Append(' ');
            line.Append(word);
        }
        if (line.Length > 0) lines.Add(line.ToString());
        return string.Join(Environment.NewLine, lines);
    }

    private static FlowLayoutPanel Row() => new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0, 8, 0, 0) };
    private static Label Caption(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(0, 16, 0, 5) };
    private static Button Button(string text, EventHandler click)
    {
        var button = new Button { Text = text, AutoSize = true, Padding = new Padding(8, 4, 8, 4), FlatStyle = FlatStyle.System };
        button.Click += click;
        return button;
    }
    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new Settings(mode.Text, path.Text, devices.SelectedItem as string, screens[monitors.SelectedIndex].DeviceName, (int)cache.Value, SelectedCaptureOptions())));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { status.Text = "Playing · could not save settings: " + ex.Message; }
    }
    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var saved = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath));
            if (saved is null) return;
            var chosen = Array.IndexOf(Modes, saved.Mode);
            mode.SelectedIndex = chosen >= 0 ? chosen : 0;
            path.Text = saved.Path;
            if (saved.Device is not null && devices.Items.Contains(saved.Device)) devices.SelectedItem = saved.Device;
            var index = Array.FindIndex(screens, s => s.DeviceName == saved.Monitor);
            if (index >= 0) monitors.SelectedIndex = index;
            cache.Value = Math.Clamp(saved.Cache, 50, 2000);
            var options = (saved.Capture ?? new CaptureOptions()).Normalize();
            format.SelectedItem = options.Format; resolution.SelectedItem = options.Resolution;
            fps.SelectedItem = options.Fps; colorSpace.SelectedItem = options.ColorSpace;
            colorRange.SelectedItem = options.ColorRange; aspect.SelectedItem = options.Aspect;
            dynamicRange.SelectedItem = options.DynamicRange; hdrPeak.SelectedItem = options.HdrPeak;
            customSize.Text = options.CustomSize; customMode.SelectedItem = options.CustomMode;
            if (sound.Items.Contains(options.Sound)) sound.SelectedItem = options.Sound;
            foreach (var cell in anchorCells) cell.Checked = (string?)cell.Tag == options.Anchor;
            UpdateAspectControls();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { status.Text = "Could not read saved settings, started with defaults."; }
    }
    private sealed record Settings(string Mode, string Path, string? Device, string Monitor, int Cache, CaptureOptions? Capture = null);
}
