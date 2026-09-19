using System.Diagnostics;
using System.Globalization;

namespace Wallcast;

internal sealed record CaptureOptions(
    string Format = "NV12", string Resolution = "1920x1080", string Fps = "60",
    string ColorSpace = "Rec.709", string ColorRange = "Limited", string Aspect = "Input resolution",
    string DynamicRange = "SDR", string HdrPeak = "1000",
    string CustomSize = "1920x1080", string CustomScale = "Fit to screen")
{
    public static readonly string[] Formats = ["NV12", "YUY2", "UYVY", "RGB24", "MJPEG"];
    public static readonly string[] Resolutions = ["1920x1080", "1280x720", "3840x2160", "2560x1440", "1920x1200", "1600x1200", "1024x768", "640x480"];
    public static readonly string[] FrameRates = ["60", "59.94", "50", "30", "29.97", "25", "24"];
    public static readonly string[] ColorSpaces = ["Rec.709", "Rec.601", "Rec.2020"];
    public static readonly string[] ColorRanges = ["Limited", "Full"];
    public static readonly string[] Aspects = ["Input resolution", "16:9", "4:3", "16:10", "Stretch to screen", "Custom size"];
    // Fit keeps the custom shape but grows it to the screen; Actual pixels places it at its own size.
    public static readonly string[] CustomScales = ["Fit to screen", "Actual pixels"];
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
        CustomScales.Contains(CustomScale) ? CustomScale : CustomScales[0]);

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
            if (Aspect == Aspects[0] || Aspect == Aspects[4]) return new Rectangle(Point.Empty, frame);
            if (Aspect == Aspects[5])
            {
                var custom = Measure(CustomSize) ?? new Size(1920, 1080);
                return CustomScale == CustomScales[1]
                    ? Even(Centre(frame, Math.Min(frame.Width, custom.Width), Math.Min(frame.Height, custom.Height)))
                    : Even(Largest(frame, (double)custom.Width / custom.Height));
            }
            return Even(Largest(frame, Aspect switch { "16:9" => 16d / 9, "4:3" => 4d / 3, _ => 1.6 }));
        }
    }

    // What actually leaves the capture engine, which is the frame minus whatever was cropped away.
    [System.Text.Json.Serialization.JsonIgnore]
    public Size OutputSize => Crop.Size;

    // Where on the monitor the picture goes. Whatever this leaves over is not covered at all, so the
    // user's own wallpaper shows there.
    public Rectangle Fit(Size target)
    {
        if (Aspect == Aspects[4]) return new Rectangle(Point.Empty, target);
        var output = OutputSize;
        // Actual pixels means exactly that: the cropped picture lands one source pixel per screen pixel.
        if (Aspect == Aspects[5] && CustomScale == CustomScales[1])
            return Centre(target, Math.Min(target.Width, output.Width), Math.Min(target.Height, output.Height));
        return Largest(target, (double)output.Width / output.Height);
    }

    // Subsampled formats cannot be cut on an odd boundary. Rounding the origin inwards rather than
    // outwards matters: rounding out drags the bar we are trying to remove back into the picture.
    private static Rectangle Even(Rectangle area)
    {
        int x = (area.X + 1) & ~1, y = (area.Y + 1) & ~1;
        return new Rectangle(x, y, (area.Width - (x - area.X)) & ~1, (area.Height - (y - area.Y)) & ~1);
    }

    private static Rectangle Largest(Size target, double ratio) => Centre(target,
        Math.Min(target.Width, (int)Math.Round(target.Height * ratio)),
        Math.Min(target.Height, (int)Math.Round(target.Width / ratio)));

    private static Rectangle Centre(Size target, int width, int height) =>
        new((target.Width - width) / 2, (target.Height - height) / 2, width, height);
}
