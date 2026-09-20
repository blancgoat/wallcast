using System.Diagnostics;
using System.Globalization;

namespace Wallcast;

internal sealed record CaptureOptions(
    string Format = "NV12", string Resolution = "1920x1080", string Fps = "60",
    string ColorSpace = "Rec.709", string ColorRange = "Limited", string Aspect = "Input resolution",
    string DynamicRange = "SDR", string HdrPeak = "1000",
    string CustomSize = "1920x1080", string CustomMode = "Fill, cropping the overflow", string Anchor = "Center",
    string Audio = "", string SoundRate = "44100")
{
    public static readonly string[] Formats = ["NV12", "YUY2", "UYVY", "RGB24", "MJPEG"];
    public static readonly string[] Resolutions = ["1920x1080", "1280x720", "3840x2160", "2560x1440", "1920x1200", "1600x1200", "1024x768", "640x480"];
    public static readonly string[] FrameRates = ["60", "59.94", "50", "30", "29.97", "25", "24"];
    public static readonly string[] ColorSpaces = ["Rec.709", "Rec.601", "Rec.2020"];
    public static readonly string[] ColorRanges = ["Limited", "Full"];
    public static readonly string[] DynamicRanges = ["SDR", "HDR10 / PQ → SDR", "HLG → SDR"];
    public static readonly string[] HdrPeaks = ["1000", "400", "600", "1600", "4000"];
    // What the sound pin is opened at. It has to be said out loud, like every other input format here:
    // a card offers several and the engine would otherwise take whichever it happens to list first.
    public static readonly string[] SoundRates = ["44100", "48000", "32000"];

    public CaptureOptions Normalize()
    {
        // Everything about where the picture lands is Placement's business, because a video file asks
        // the same four questions and there is no reason to answer them twice.
        var layout = Layout.Normalize();
        return new(
            Formats.Contains(Format) ? Format : "NV12",
            Resolutions.Contains(Resolution) ? Resolution : "1920x1080",
            FrameRates.Contains(Fps) ? Fps : "60",
            ColorSpaces.Contains(ColorSpace) ? ColorSpace : "Rec.709",
            ColorRanges.Contains(ColorRange) ? ColorRange : ColorRanges[0],
            layout.Aspect,
            DynamicRanges.Contains(DynamicRange) ? DynamicRange : DynamicRanges[0],
            HdrPeaks.Contains(HdrPeak) ? HdrPeak : "1000",
            layout.CustomSize, layout.CustomMode, layout.Anchor,
            (Audio ?? "").Trim(),
            SoundRates.Contains(SoundRate) ? SoundRate : SoundRates[0]);
    }

    // An audio device the engine named, or empty for a silent capture. It is kept apart from the video
    // device because the two need not be the same thing: a capture card carries its own sound, while a
    // virtual camera has none at all and has to borrow a virtual audio device.
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasSound => Audio.Length > 0;

    // The geometry half of these settings, in the form both inputs share.
    [System.Text.Json.Serialization.JsonIgnore]
    public Placement Layout => new(Aspect, CustomSize, CustomMode, Anchor);

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
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
            // Sound leaves on stdout. The picture cannot: a redirected stdout pipe stalls long before
            // 4K BGRA needs it to, which is why that went to a named pipe. Stereo PCM is a thousandth
            // of the traffic and never comes close.
            RedirectStandardOutput = HasSound
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
        // Both halves of the sound format, because asking for only the rate is worse than asking for
        // nothing: the engine takes the first pin format that matches, and on a card that offers 7.1
        // that is the eight channel one. Two channels arriving as eight is most of what "the sound is
        // wrong" turns out to mean.
        if (HasSound)
        {
            info.ArgumentList.Add("-channels"); info.ArgumentList.Add("2");
            info.ArgumentList.Add("-sample_rate"); info.ArgumentList.Add(SoundRate);
        }
        if (Format == "MJPEG") { info.ArgumentList.Add("-vcodec"); info.ArgumentList.Add("mjpeg"); }
        else
        {
            info.ArgumentList.Add("-pixel_format");
            info.ArgumentList.Add(Format switch { "YUY2" => "yuyv422", "UYVY" => "uyvy422", "RGB24" => "bgr24", _ => "nv12" });
        }
        // One input, both pins. A card that carries its sound alongside the picture will not hand it
        // over as a device of its own, so asking for it separately fails to connect the pins at all.
        foreach (var arg in new[] { "-i", HasSound ? $"video={device}:audio={Audio}" : "video=" + device,
            "-map", "0:v", "-an", "-sn", "-dn", "-vf", ConversionFilter,
            "-fps_mode", "passthrough", "-threads", "2", "-f", "rawvideo", "-pix_fmt", "bgra", output }) info.ArgumentList.Add(arg);
        // Uncompressed, because the player is in the same box and anything else would only cost latency.
        // The channel count is pinned again on the way out, so a device that could only be opened in
        // some other layout still reaches the player as the stereo it will be played as.
        if (HasSound)
            foreach (var arg in new[] { "-map", "0:a", "-vn", "-ac", "2", "-c:a", "pcm_s16le", "-f", "wav", "pipe:1" })
                info.ArgumentList.Add(arg);
        return info;
    }

    // Cutting in ffmpeg rather than on screen also keeps the bars off the pipe instead of paying to
    // carry them, which is why the capture path crops at the source and the video path does not.
    [System.Text.Json.Serialization.JsonIgnore]
    public Rectangle Crop => Layout.Crop(FrameSize);

    [System.Text.Json.Serialization.JsonIgnore]
    public Size OutputSize => Crop.Size;

    public Rectangle Fit(Size target) => Layout.Fit(FrameSize, target);
}
