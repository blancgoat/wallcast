using System.Text.Json;

namespace Still;

internal sealed class MainForm : Form
{
    private readonly ComboBox mode = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox path = new() { ReadOnly = true, PlaceholderText = "동영상 파일을 선택하세요" };
    private readonly ComboBox devices = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox monitors = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly NumericUpDown cache = new() { Minimum = 50, Maximum = 2000, Increment = 50, Value = 150 };
    private readonly CheckBox mute = new() { Text = "동영상 음소거", Checked = true, AutoSize = true };
    private readonly Label status = new() { Text = "입력을 선택하고 적용하세요.", AutoSize = true, MaximumSize = new Size(490, 0) };
    private readonly FlowLayoutPanel fileRow = Row();
    private readonly FlowLayoutPanel captureRow = Row();
    private readonly FlowLayoutPanel cacheRow = Row();
    private readonly TableLayoutPanel captureSettings = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Margin = new Padding(0, 8, 0, 0) };
    private readonly ComboBox format = Choice(CaptureOptions.Formats);
    private readonly ComboBox resolution = Choice(CaptureOptions.Resolutions);
    private readonly ComboBox fps = Choice(CaptureOptions.FrameRates);
    private readonly ComboBox colorSpace = Choice(CaptureOptions.ColorSpaces);
    private readonly ComboBox colorRange = Choice(CaptureOptions.ColorRanges);
    private readonly ComboBox aspect = Choice(CaptureOptions.Aspects);
    private readonly ComboBox dynamicRange = Choice(CaptureOptions.DynamicRanges);
    private readonly ComboBox hdrPeak = Choice(CaptureOptions.HdrPeaks);
    private readonly NotifyIcon tray;
    private readonly System.Windows.Forms.Timer watchdog = new() { Interval = 2000 };
    private Playback? playback;
    private bool exiting;
    private Screen[] screens = [];
    private static readonly string SettingsPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Still", "settings.json");

    public MainForm()
    {
        SuspendLayout();
        AutoScaleMode = AutoScaleMode.None;
        Text = "Still · 움직이는 바탕화면";
        AutoScaleDimensions = new SizeF(96, 96);
        ClientSize = new Size(580, 790);
        MinimumSize = new Size(580, 610);
        Font = new Font("맑은 고딕", 10);
        BackColor = Color.FromArgb(246, 247, 250);
        var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(28), AutoScroll = true };
        Controls.Add(body);
        body.Controls.Add(new Label { Text = "Still", Font = new Font("Segoe UI", 28, FontStyle.Bold), AutoSize = true });
        body.Controls.Add(new Label { Text = "원하는 화면을, 바탕화면으로.", AutoSize = true, Margin = new Padding(0, 0, 0, 22) });
        body.Controls.Add(Caption("입력 소스"));
        mode.Items.AddRange(["동영상 파일", "캡처보드 / 가상 카메라"]);
        mode.Width = 490;
        mode.SelectedIndex = 0;
        body.Controls.Add(mode);
        path.Width = 365;
        fileRow.Controls.Add(path);
        fileRow.Controls.Add(Button("파일 선택", PickFile));
        body.Controls.Add(fileRow);
        devices.Width = 365;
        captureRow.Controls.Add(devices);
        captureRow.Controls.Add(Button("새로 고침", (_, _) => RefreshDevices()));
        body.Controls.Add(captureRow);
        AddCaptureSetting("영상 형식", format);
        AddCaptureSetting("해상도", resolution);
        AddCaptureSetting("프레임 속도", fps);
        AddCaptureSetting("색 공간", colorSpace);
        AddCaptureSetting("색 범위", colorRange);
        AddCaptureSetting("입력 HDR", dynamicRange);
        AddCaptureSetting("HDR 최대 밝기 (nit)", hdrPeak);
        AddCaptureSetting("표시 비율", aspect);
        dynamicRange.SelectedIndexChanged += (_, _) => UpdateHdrControls();
        UpdateHdrControls();
        body.Controls.Add(captureSettings);
        cacheRow.Controls.Add(new Label { Text = "캡처 버퍼 (ms)", AutoSize = true, Padding = new Padding(0, 5, 10, 0) });
        cacheRow.Controls.Add(cache);
        body.Controls.Add(cacheRow);
        body.Controls.Add(Caption("출력 모니터"));
        monitors.Width = 490;
        body.Controls.Add(monitors);
        mute.Margin = new Padding(0, 14, 0, 10);
        body.Controls.Add(mute);
        body.Controls.Add(new Label { Text = "원본 비율 유지 · 동영상 자동 반복 · 캡처는 영상만 출력", AutoSize = true, ForeColor = Color.DimGray });
        var actions = Row();
        actions.Margin = new Padding(0, 20, 0, 12);
        actions.Controls.Add(Button("바탕화면 적용", Apply));
        actions.Controls.Add(Button("중지", (_, _) => Stop()));
        actions.Controls.Add(Button("트레이로 숨기기", (_, _) => Hide()));
        body.Controls.Add(actions);
        body.Controls.Add(status);
        mode.SelectedIndexChanged += (_, _) => UpdateMode();
        mute.CheckedChanged += (_, _) => playback?.SetMute(mute.Checked);
        var menu = new ContextMenuStrip();
        menu.Items.Add("Still 열기", null, (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); });
        menu.Items.Add("배경 중지", null, (_, _) => Stop());
        menu.Items.Add("종료", null, (_, _) => { exiting = true; Close(); });
        tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "Still · 바탕화면", Visible = true, ContextMenuStrip = menu };
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
                status.Text = "바탕화면 또는 모니터 구성이 바뀌었습니다. 다시 적용해 주세요.";
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
        catch (Exception ex) { status.Text = "재생 엔진 초기화 실패: " + ex.Message; }
    }

    private void RefreshMonitors()
    {
        var old = monitors.SelectedIndex;
        screens = Screen.AllScreens;
        monitors.Items.Clear();
        foreach (var screen in screens) monitors.Items.Add($"{screen.DeviceName} · {screen.Bounds.Width} × {screen.Bounds.Height}{(screen.Primary ? " · 기본" : "")}");
        if (screens.Length > 0) monitors.SelectedIndex = Math.Clamp(old, 0, screens.Length - 1);
    }

    private void RefreshDevices()
    {
        var old = devices.SelectedItem as string;
        devices.Items.Clear();
        try
        {
            devices.Items.AddRange(CaptureDevices.Enumerate().Cast<object>().ToArray());
            if (old is not null && devices.Items.Contains(old)) devices.SelectedItem = old;
            else if (devices.Items.Count > 0) devices.SelectedIndex = 0;
            else status.Text = "캡처 장치가 없습니다. 장치를 연결한 뒤 새로 고침하세요.";
        }
        catch (Exception ex) { status.Text = "장치 검색 실패: " + ex.Message; }
    }

    private void PickFile(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog { Title = "배경 동영상 선택", Filter = "동영상|*.mp4;*.mkv;*.mov;*.webm;*.avi;*.m4v;*.wmv;*.ts|모든 파일|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(this) == DialogResult.OK) path.Text = dialog.FileName;
    }

    private void Apply(object? sender, EventArgs e)
    {
        try
        {
            if (playback is null) throw new InvalidOperationException("재생 엔진을 사용할 수 없습니다. 앱을 다시 실행해 주세요.");
            if (monitors.SelectedIndex < 0) throw new InvalidOperationException("모니터를 선택해 주세요.");
            IWallpaperSource input;
            if (mode.SelectedIndex == 0)
            {
                if (!File.Exists(path.Text)) throw new InvalidOperationException("동영상 파일을 선택해 주세요.");
                input = new VideoSource(path.Text);
            }
            else
            {
                if (devices.SelectedItem is not string name) throw new InvalidOperationException("캡처 장치를 선택해 주세요.");
                input = new CaptureSource(name, (int)cache.Value, SelectedCaptureOptions());
            }
            status.Text = "입력을 여는 중…";
            playback.Start(input, screens[monitors.SelectedIndex], mute.Checked);
            SaveSettings();
        }
        catch (Exception ex) { status.Text = ex.Message; }
    }

    private void Stop() { playback?.Stop(); status.Text = "중지됨 · 기존 바탕화면으로 돌아왔습니다."; }
    private void UpdateMode()
    {
        var parent = captureRow.Parent;
        parent?.SuspendLayout();
        fileRow.Visible = mode.SelectedIndex == 0;
        captureRow.Visible = cacheRow.Visible = captureSettings.Visible = mode.SelectedIndex == 1;
        mute.Enabled = mode.SelectedIndex == 0;
        if (parent is not null) parent.Controls.SetChildIndex(captureRow, parent.Controls.GetChildIndex(mode) + 1);
        parent?.ResumeLayout(true);
    }
    private static ComboBox Choice(string[] values)
    {
        var choice = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 325, Margin = new Padding(3, 4, 3, 4) };
        choice.Items.AddRange(values); choice.SelectedIndex = 0;
        return choice;
    }
    private void AddCaptureSetting(string text, Control control)
    {
        var row = captureSettings.RowCount++;
        captureSettings.Controls.Add(new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 22, 0) }, 0, row);
        captureSettings.Controls.Add(control, 1, row);
    }
    private void UpdateHdrControls()
    {
        var hdr = dynamicRange.SelectedIndex > 0;
        hdrPeak.Enabled = hdr;
        colorSpace.Enabled = !hdr;
        if (hdr) colorSpace.SelectedItem = "Rec.2020";
        else if (colorSpace.Text == "Rec.2020") colorSpace.SelectedItem = "Rec.709";
    }
    private CaptureOptions SelectedCaptureOptions() => new(format.Text, resolution.Text, fps.Text, colorSpace.Text, colorRange.Text, aspect.Text, dynamicRange.Text, hdrPeak.Text);
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
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new Settings(mode.SelectedIndex, path.Text, devices.SelectedItem as string, screens[monitors.SelectedIndex].DeviceName, (int)cache.Value, mute.Checked, SelectedCaptureOptions())));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { status.Text = "재생 중 · 설정 저장 실패: " + ex.Message; }
    }
    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var saved = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath));
            if (saved is null) return;
            mode.SelectedIndex = Math.Clamp(saved.Mode, 0, 1); path.Text = saved.Path;
            if (saved.Device is not null && devices.Items.Contains(saved.Device)) devices.SelectedItem = saved.Device;
            var index = Array.FindIndex(screens, s => s.DeviceName == saved.Monitor);
            if (index >= 0) monitors.SelectedIndex = index;
            cache.Value = Math.Clamp(saved.Cache, 50, 2000); mute.Checked = saved.Mute;
            var options = (saved.Capture ?? new CaptureOptions()).Normalize();
            format.SelectedItem = options.Format; resolution.SelectedItem = options.Resolution;
            fps.SelectedItem = options.Fps; colorSpace.SelectedItem = options.ColorSpace;
            colorRange.SelectedItem = options.ColorRange; aspect.SelectedItem = options.Aspect;
            dynamicRange.SelectedItem = options.DynamicRange; hdrPeak.SelectedItem = options.HdrPeak;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { status.Text = "저장된 설정을 읽지 못해 기본값으로 시작했습니다."; }
    }
    private sealed record Settings(int Mode, string Path, string? Device, string Monitor, int Cache, bool Mute, CaptureOptions? Capture = null);
}
