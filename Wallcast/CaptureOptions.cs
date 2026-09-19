using System.Diagnostics;
using System.Globalization;

namespace Wallcast;

internal sealed record CaptureOptions(
    string Format = "NV12", string Resolution = "1920x1080", string Fps = "60",
    string ColorSpace = "Rec.709", string ColorRange = "Limited", string Aspect = "Input resolution",
    string DynamicRange = "SDR", string HdrPeak = "1000",
    string CustomSize = "1920x1080", string CustomMode = "Fill, cropping the overflow", string Anchor = "Center")
{
    public static readonly string[] Formats = ["NV12", "YUY2", "UYVY", "RGB24", "MJPEG"];
    public static readonly string[] Resolutions = ["1920x1080", "1280x720", "3840x2160", "2560x1440", "1920x1200", "1600x1200", "1024x768", "640x480"];
    public static readonly string[] FrameRates = ["60", "59.94", "50", "30", "29.97", "25", "24"];
    public static readonly string[] ColorSpaces = ["Rec.709", "Rec.601", "Rec.2020"];
    public static readonly string[] ColorRanges = ["Limited", "Full"];
    // Fixed ratios are gone on purpose. Under crop they would only ever be a worse-spelled custom
    // size, and the day a fixed ratio is genuinely wanted it will want to scale, not crop.
    public const string AsCaptured = "Input resolution", Stretch = "Stretch to screen", Custom = "Output resolution";
    public static readonly string[] Aspects = [AsCaptured, Stretch, Custom];
    // An output resolution, the way OBS means it: the picture comes out at this size whatever the
    // capture resolution is. The mode says how the frame is mapped into it, and the frame is always
    // centred inside it, so there is no second position to choose.
    public const string FillCrop = "Fill, cropping the overflow", FitPad = "Fit, padding the gap",
        Centred = "Centre at 1:1, padding the gap", StretchFill = "Stretch to fill, distorting";
    public static readonly string[] CustomModes = [FillCrop, FitPad, Centred, StretchFill];
    // Where the picture sits on the monitor, laid out like a canvas-size anchor. It only bites where
    // the picture leaves room: a 4:3 picture on a 16:9 screen can slide sideways but not up or down.
    public static readonly string[] Anchors =
        ["Top left", "Top", "Top right", "Left", "Center", "Right", "Bottom left", "Bottom", "Bottom right"];
    public static readonly string[] DynamicRanges = ["SDR", "HDR10 / PQ → SDR", "HLG → SDR"];
    public static readonly string[] HdrPeaks = ["1000", "400", "600", "1600", "4000"];

    public CaptureOptions Normalize() => new(
        Formats.Contains(Format) ? Format : "NV12",
        Resolutions.Contains(Resolution) ? Resolution : "1920x1080",
        FrameRates.Contains(Fps) ? Fps : "60",
        ColorSpaces.Contains(ColorSpace) ? ColorSpace : "Rec.709",
        ColorRanges.Contains(ColorRange) ? ColorRange : ColorRanges[0],
        Aspects.Contains(Aspect) ? Aspect : Aspects[0],
        DynamicRanges.Contains(DynamicRange) ? DynamicRange : DynamicRanges[0],
        HdrPeaks.Contains(HdrPeak) ? HdrPeak : "1000",
        Measure(CustomSize) is { } size ? $"{size.Width}x{size.Height}" : "1920x1080",
        CustomModes.Contains(CustomMode) ? CustomMode : CustomModes[0],
        Anchors.Contains(Anchor) ? Anchor : "Center");

    // Accepts 1920x1080 and 1920 x 1080 alike, and rejects anything that is not a usable frame.
    public static Size? Measure(string value)
    {
        var parts = (value ?? "").Split('x', 'X', '*', '×');
        if (parts.Length != 2) return null;
        if (!int.TryParse(parts[0].Trim(), out var width) || !int.TryParse(parts[1].Trim(), out var height)) return null;
        if (width < 16 || height < 16 || width > 16384 || height > 16384) return null;
        return new Size(width, height);
    }
    // Derived, so it is not part of the saved settings.
    [System.Text.Json.Serialization.JsonIgnore]
    public Size FrameSize
    {
        get { var parts = Resolution.Split('x'); return new Size(int.Parse(parts[0]), int.Parse(parts[1])); }
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public string ConversionFilter
    {
        get
        {
            var cut = Crop;
            // Cropping first also keeps the bars off the pipe instead of paying to carry them.
            var trim = cut.Location.IsEmpty && cut.Size == FrameSize ? "" : $"crop={cut.Width}:{cut.Height}:{cut.X}:{cut.Y},";
            if (DynamicRange == "SDR") return $"{trim}scale=in_color_matrix={Matrix}:in_range={(ColorRange == ColorRanges[1] ? "pc" : "tv")}:out_range=pc:flags=fast_bilinear,format=bgra,setsar=1";
            var transfer = DynamicRange == DynamicRanges[1] ? "smpte2084" : "arib-std-b67";
            var range = ColorRange == ColorRanges[1] ? "full" : "limited";
            var matrix = Format == "RGB24" ? "gbr" : "2020_ncl";
            // Decode HDR transfer in linear light, convert BT.2020 primaries, then map highlights.
            // A 100-nit SDR reference makes the explicit peak (nits / 100) deterministic without metadata.
            var planar = Format == "RGB24" ? "gbrp" : "yuv444p16le";
            return $"{trim}format={planar},zscale=matrixin={matrix}:transferin={transfer}:primariesin=2020:rangein={range}:transfer=linear:matrix=gbr:primaries=2020:range=full:npl=100," +
                $"format=gbrpf32le,zscale=primaries=709,tonemap=tonemap=mobius:param=0.3:desat=2:peak={int.Parse(HdrPeak) / 100}," +
                "zscale=transfer=709:matrix=gbr:range=full,format=gbrp,format=bgra,setsar=1";
        }
    }
    private string Matrix => ColorSpace switch { "Rec.601" => "bt601", "Rec.2020" => "bt2020", _ => "bt709" };

    // The DirectShow queue holds frames in the device's own format, not the converted BGRA output.
    private double InputBytesPerPixel => Format switch { "YUY2" or "UYVY" => 2, "RGB24" => 3, "MJPEG" => 1, _ => 1.5 };

    public ProcessStartInfo CreateStartInfo(string device, int bufferMilliseconds, string output)
    {
        if (string.IsNullOrWhiteSpace(device)) throw new InvalidOperationException("Select a capture device.");
        if (this != Normalize()) throw new InvalidOperationException("Unsupported capture settings.");
        var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "capture", "ffmpeg.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true
        };
        var frameSize = FrameSize;
        // Bound the DirectShow queue; the renderer separately keeps only the newest frame. Sizing this
        // from the output format would buffer far more than the requested milliseconds, and every extra
        // queued frame is latency once the reader cannot keep up.
        var frameBytes = (long)(frameSize.Width * frameSize.Height * InputBytesPerPixel);
        var rate = double.Parse(Fps, CultureInfo.InvariantCulture);
        var bytes = Math.Clamp((long)(frameBytes * rate * bufferMilliseconds / 1000), frameBytes * 3, 512_000_000);
        string[] args = ["-hide_banner", "-y", "-loglevel", "info", "-nostdin", "-f", "dshow", "-rtbufsize", bytes.ToString(),
            "-video_size", Resolution, "-framerate", Fps];
        foreach (var arg in args) info.ArgumentList.Add(arg);
        if (Format == "MJPEG") { info.ArgumentList.Add("-vcodec"); info.ArgumentList.Add("mjpeg"); }
        else
        {
            info.ArgumentList.Add("-pixel_format");
            info.ArgumentList.Add(Format switch { "YUY2" => "yuyv422", "UYVY" => "uyvy422", "RGB24" => "bgr24", _ => "nv12" });
        }
        foreach (var arg in new[] { "-i", "video=" + device, "-an", "-sn", "-dn", "-vf", ConversionFilter,
            "-fps_mode", "passthrough", "-threads", "2", "-f", "rawvideo", "-pix_fmt", "bgra", output }) info.ArgumentList.Add(arg);
        return info;
    }

    // A capture card pillarboxes a 4:3 source into its 16:9 frame, so the bars are part of what it
    // sends. They come off by cutting them out of the frame, never by squeezing the picture: every
    // choice here but "Stretch to screen" keeps the picture's own shape.
    [System.Text.Json.Serialization.JsonIgnore]
    public Rectangle Crop
    {
        get
        {
            var frame = FrameSize;
            // Only filling and 1:1 leave anything on the cutting room floor. Fit and stretch both keep
            // the whole frame; they differ in whether the gap is padded or the picture is distorted.
            if (Aspect != Custom || CustomMode is FitPad or StretchFill) return new Rectangle(Point.Empty, frame);
            var output = Measure(CustomSize) ?? new Size(1920, 1080);
            // Sizing before centring keeps the cut exact; rounding a placed rectangle drags the bar we
            // are removing back into the picture. The bars sit either side, so the cut is centred.
            var size = CustomMode == Centred
                ? new Size(Math.Min(frame.Width, output.Width), Math.Min(frame.Height, output.Height))
                : Largest(frame, (double)output.Width / output.Height);
            size = new Size(size.Width & ~1, size.Height & ~1);
            return new Rectangle((frame.Width - size.Width) / 2 & ~1, (frame.Height - size.Height) / 2 & ~1,
                size.Width, size.Height);
        }
    }

    // 0 hugs the near edge, 2 the far edge, 1 sits in the middle of whatever room is left over.
    private static int Place(int slack, int position) => position switch { 0 => 0, 2 => slack, _ => slack / 2 };

    // What actually leaves the capture engine, which is the frame minus whatever was cropped away.
    [System.Text.Json.Serialization.JsonIgnore]
    public Size OutputSize => Crop.Size;

    // Where on the monitor the picture goes. Whatever this leaves over is not covered at all, so the
    // user's own wallpaper shows there.
    public Rectangle Fit(Size target)
    {
        if (Aspect == Stretch) return new Rectangle(Point.Empty, target);
        var cut = OutputSize;
        var size = Aspect == Custom ? Canvas(cut) : Largest(target, (double)cut.Width / cut.Height);
        // A monitor smaller than the requested output still has to show all of it, shape intact.
        size = Bound(size, target);
        var cell = Math.Max(0, Array.IndexOf(Anchors, Anchor));
        return new Rectangle(Place(target.Width - size.Width, cell % 3), Place(target.Height - size.Height, cell / 3),
            size.Width, size.Height);
    }

    // The output resolution is the whole point: filling and stretching land on it exactly, fitting and
    // 1:1 land inside it. The gap is left uncovered rather than painted, so the wallpaper fills it.
    private Size Canvas(Size cut)
    {
        var output = Measure(CustomSize) ?? new Size(1920, 1080);
        if (CustomMode is FillCrop or StretchFill) return output;
        if (CustomMode == Centred) return new Size(Math.Min(output.Width, cut.Width), Math.Min(output.Height, cut.Height));
        var scale = Math.Min((double)output.Width / cut.Width, (double)output.Height / cut.Height);
        return new Size(Math.Max(1, (int)Math.Round(cut.Width * scale)), Math.Max(1, (int)Math.Round(cut.Height * scale)));
    }

    private static Size Bound(Size want, Size target)
    {
        if (want.Width <= target.Width && want.Height <= target.Height) return want;
        var scale = Math.Min((double)target.Width / want.Width, (double)target.Height / want.Height);
        return new Size(Math.Max(1, (int)Math.Round(want.Width * scale)), Math.Max(1, (int)Math.Round(want.Height * scale)));
    }

    private static Size Largest(Size target, double ratio) => new(
        Math.Min(target.Width, (int)Math.Round(target.Height * ratio)),
        Math.Min(target.Height, (int)Math.Round(target.Width / ratio)));

}
