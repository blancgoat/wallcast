using LibVLCSharp.Shared;

namespace Wallcast;

// A new input type only needs to supply media and its playback policy.
internal interface IWallpaperSource
{
    bool Loop { get; }
}

// Frame is the video's display size, which the form probes before playing so the same placement
// arithmetic the capture path uses has something to work from. Null means the probe found nothing,
// and the picture then simply takes the monitor.
internal sealed record VideoSource(string Path, Placement? Layout = null, Size? Frame = null) : IWallpaperSource
{
    public bool Loop => true;
    public Media Open(LibVLC engine)
    {
        if (!File.Exists(Path)) throw new FileNotFoundException("Video file not found.", Path);
        var media = new Media(engine, new Uri(System.IO.Path.GetFullPath(Path)));
        // Looping by restarting the player tears the video output down and builds it again, and for
        // the frame that takes, Explorer's wallpaper shows through the hole. Repeating the input
        // keeps the same output alive across the seam. The count is a ceiling, not a plan: even a
        // ten second clip would have to run for days to reach it, and EndReached restarts if it does.
        media.AddOption(":input-repeat=65535");
        return media;
    }
}

internal sealed record CaptureSource(string Device, int CacheMilliseconds, CaptureOptions? Options = null) : IWallpaperSource
{
    public bool Loop => false;
}
