namespace Wallcast;

// Where a picture lands on the monitor. A capture card and a video file both arrive as a frame of
// known size and ask the same three questions of it - what shape, how big, and where - so the
// arithmetic lives here instead of being written twice and drifting apart.
internal sealed record Placement(
    string Aspect = Placement.AsCaptured, string CustomSize = "1920x1080",
    string CustomMode = Placement.FillCrop, string Anchor = "Center")
{
    // Fixed ratios are gone on purpose. Under crop they would only ever be a worse-spelled custom
    // size, and the day a fixed ratio is genuinely wanted it will want to scale, not crop.
    public const string AsCaptured = "Input resolution", Stretch = "Stretch to screen", Custom = "Output resolution";
    public static readonly string[] Aspects = [AsCaptured, Stretch, Custom];
    // An output resolution, the way OBS means it: the picture comes out at this size whatever the
    // source resolution is. The mode says how the frame is mapped into it, and the frame is always
    // centred inside it, so there is no second position to choose.
    public const string FillCrop = "Fill, cropping the overflow", FitPad = "Fit, padding the gap",
        Centred = "Centre at 1:1, padding the gap", StretchFill = "Stretch to fill, distorting";
    public static readonly string[] CustomModes = [FillCrop, FitPad, Centred, StretchFill];
    // Where the picture sits on the monitor, laid out like a canvas-size anchor. It only bites where
    // the picture leaves room: a 4:3 picture on a 16:9 screen can slide sideways but not up or down.
    public static readonly string[] Anchors =
        ["Top left", "Top", "Top right", "Left", "Center", "Right", "Bottom left", "Bottom", "Bottom right"];

    public Placement Normalize() => new(
        Aspects.Contains(Aspect) ? Aspect : Aspects[0],
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

    // A capture card pillarboxes a 4:3 source into its 16:9 frame, so the bars are part of what it
    // sends, and a video file can carry them baked in just the same. They come off by cutting them
    // out of the frame, never by squeezing the picture: every choice here but the two stretch modes
    // keeps the picture's own shape.
    public Rectangle Crop(Size frame)
    {
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

    // What actually reaches the screen, which is the frame minus whatever was cropped away.
    public Size Output(Size frame) => Crop(frame).Size;

    // Where on the monitor the picture goes. Whatever this leaves over is not covered at all, so the
    // user's own wallpaper shows there.
    public Rectangle Fit(Size frame, Size target)
    {
        if (Aspect == Stretch) return new Rectangle(Point.Empty, target);
        var cut = Output(frame);
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

    // 0 hugs the near edge, 2 the far edge, 1 sits in the middle of whatever room is left over.
    private static int Place(int slack, int position) => position switch { 0 => 0, 2 => slack, _ => slack / 2 };

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
