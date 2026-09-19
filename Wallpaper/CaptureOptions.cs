using System.Diagnostics;

namespace Still;

internal sealed record CaptureOptions(
    string Format = "NV12", string Resolution = "1920x1080", string Fps = "60",
    string ColorSpace = "Rec.709", string ColorRange = "제한 (Limited)", string Aspect = "입력 해상도",
    string DynamicRange = "SDR", string HdrPeak = "1000")
{
    public static readonly string[] Formats = ["NV12", "YUY2", "UYVY", "RGB24", "MJPEG"];
    public static readonly string[] Resolutions = ["1920x1080", "1280x720", "3840x2160", "2560x1440", "1920x1200", "1600x1200", "1024x768", "640x480"];
    public static readonly string[] FrameRates = ["60", "59.94", "50", "30", "29.97", "25", "24"];
    public static readonly string[] ColorSpaces = ["Rec.709", "Rec.601", "Rec.2020"];
    public static readonly string[] ColorRanges = ["제한 (Limited)", "전체 (Full)"];
    public static readonly string[] Aspects = ["입력 해상도", "16:9", "4:3", "16:10", "화면에 늘이기"];
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
        HdrPeaks.Contains(HdrPeak) ? HdrPeak : "1000");
    public Size FrameSize
    {
        get { var parts = Resolution.Split('x'); return new Size(int.Parse(parts[0]), int.Parse(parts[1])); }
    }

    public string ConversionFilter
    {
        get
        {
            if (DynamicRange == "SDR") return $"scale=in_color_matrix={Matrix}:in_range={(ColorRange == ColorRanges[1] ? "pc" : "tv")}:out_range=pc:flags=fast_bilinear,format=bgra,setsar=1";
            var transfer = DynamicRange == DynamicRanges[1] ? "smpte2084" : "arib-std-b67";
            var range = ColorRange == ColorRanges[1] ? "full" : "limited";
            var matrix = Format == "RGB24" ? "gbr" : "2020_ncl";
            // Decode HDR transfer in linear light, convert BT.2020 primaries, then map highlights.
            // A 100-nit SDR reference makes the explicit peak (nits / 100) deterministic without metadata.
            var planar = Format == "RGB24" ? "gbrp" : "yuv444p16le";
            return $"format={planar},zscale=matrixin={matrix}:transferin={transfer}:primariesin=2020:rangein={range}:transfer=linear:matrix=gbr:primaries=2020:range=full:npl=100," +
                $"format=gbrpf32le,zscale=primaries=709,tonemap=tonemap=mobius:param=0.3:desat=2:peak={int.Parse(HdrPeak) / 100}," +
                "zscale=transfer=709:matrix=gbr:range=full,format=gbrp,format=bgra,setsar=1";
        }
    }
    private string Matrix => ColorSpace switch { "Rec.601" => "bt601", "Rec.2020" => "bt2020", _ => "bt709" };

    public ProcessStartInfo CreateStartInfo(string device, int bufferMilliseconds)
    {
        if (string.IsNullOrWhiteSpace(device)) throw new InvalidOperationException("캡처 장치를 선택해 주세요.");
        if (this != Normalize()) throw new InvalidOperationException("지원하지 않는 캡처 설정입니다.");
        var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "capture", "ffmpeg.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        var frameSize = FrameSize;
        // Bound the DirectShow queue; the renderer separately keeps only the newest frame.
        var bytes = Math.Clamp((long)frameSize.Width * frameSize.Height * 4 * 60 * bufferMilliseconds / 1000, 16_000_000, 512_000_000);
        string[] args = ["-hide_banner", "-loglevel", "info", "-nostdin", "-f", "dshow", "-rtbufsize", bytes.ToString(),
            "-video_size", Resolution, "-framerate", Fps];
        foreach (var arg in args) info.ArgumentList.Add(arg);
        if (Format == "MJPEG") { info.ArgumentList.Add("-vcodec"); info.ArgumentList.Add("mjpeg"); }
        else
        {
            info.ArgumentList.Add("-pixel_format");
            info.ArgumentList.Add(Format switch { "YUY2" => "yuyv422", "UYVY" => "uyvy422", "RGB24" => "bgr24", _ => "nv12" });
        }
        foreach (var arg in new[] { "-i", "video=" + device, "-an", "-sn", "-dn", "-vf", ConversionFilter,
            "-fps_mode", "passthrough", "-threads", "2", "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1" }) info.ArgumentList.Add(arg);
        return info;
    }

    public Rectangle Fit(Size target)
    {
        if (Aspect == "화면에 늘이기") return new Rectangle(Point.Empty, target);
        double ratio = Aspect switch { "16:9" => 16d / 9, "4:3" => 4d / 3, "16:10" => 1.6, _ => (double)FrameSize.Width / FrameSize.Height };
        var width = Math.Min(target.Width, (int)Math.Round(target.Height * ratio));
        var height = Math.Min(target.Height, (int)Math.Round(target.Width / ratio));
        return new Rectangle((target.Width - width) / 2, (target.Height - height) / 2, width, height);
    }
}
