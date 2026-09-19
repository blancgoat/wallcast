using LibVLCSharp.Shared;

namespace Wallcast;

// A new input type only needs to supply media and its playback policy.
internal interface IWallpaperSource
{
    bool Loop { get; }
}

internal sealed record VideoSource(string Path) : IWallpaperSource
{
    public bool Loop => true;
    public Media Open(LibVLC engine)
    {
        if (!File.Exists(Path)) throw new FileNotFoundException("Video file not found.", Path);
        return new Media(engine, new Uri(System.IO.Path.GetFullPath(Path)));
    }
}

internal sealed record CaptureSource(string Device, int CacheMilliseconds, CaptureOptions? Options = null) : IWallpaperSource
{
    public bool Loop => false;
}
