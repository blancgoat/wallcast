using LibVLCSharp.Shared;
using System.Collections.Concurrent;

namespace Wallcast;

internal sealed class Playback : IDisposable
{
    private readonly Control dispatcher;
    private readonly LibVLC engine;
    private MediaPlayer? player;
    private CapturePlayback? capture;
    private MediaPlayer? sound;
    private DesktopHost? host;
    private IWallpaperSource? source;
    private int generation;
    private readonly ConcurrentQueue<string> errors = new();
    public event Action<string>? Status;
    public bool IsDesktopAlive => host is null || host.IsDesktopAlive;

    public Playback(Control dispatcher)
    {
        this.dispatcher = dispatcher;
        Core.Initialize();
        engine = new LibVLC("--no-video-title-show", "--no-osd");
        engine.Log += (sender, e) =>
        {
            if (e.Level != LogLevel.Error) return;
            errors.Enqueue($"{e.Module}: {e.Message}");
            while (errors.Count > 8) errors.TryDequeue(out _);
        };
    }

    public void Start(IWallpaperSource input, Screen screen, bool mute)
    {
        Stop();
        errors.Clear();
        var current = generation;
        try
        {
            source = input;
            host = new DesktopHost();
            if (input is CaptureSource device)
            {
                var options = (device.Options ?? new CaptureOptions()).Normalize();
                // Cover only the picture. The surround is left alone, so the wallpaper shows there.
                var area = options.Fit(screen.Bounds.Size);
                area.Offset(screen.Bounds.Location);
                host.Attach(screen, area);
                capture = new CapturePlayback(device.Device, device.CacheMilliseconds, options);
                capture.Started += () => Post(current, () => Status?.Invoke($"Playing · {options.Format} / {options.ColorSpace} / {options.DynamicRange} / {options.Resolution} / {options.Fps}fps"
                    + (options.HasSound ? $" · sound from {options.Audio} at {options.SoundRate}Hz" : "")));
                capture.Failed += detail => Post(current, () =>
                {
                    // A device that turns out not to hand over the audio it was asked for must not cost
                    // the picture as well. Nothing has been seen yet, so there is nothing to interrupt.
                    if (options.HasSound && capture?.Frames == 0)
                    {
                        Start(device with { Options = options with { Audio = "" } }, screen, mute);
                        Status?.Invoke($"{options.Audio} gave no sound, so the picture is playing without it.");
                        return;
                    }
                    Stop();
                    Status?.Invoke(detail);
                });
                var surface = new CaptureSurface(capture);
                surface.Failed += detail => Post(current, () => { Stop(); Status?.Invoke(detail); });
                host.Controls.Add(surface);
                capture.Start();
                // Started before the picture is asked for, so nothing is left filling the engine's
                // stdout while waiting: a full pipe there would stall the picture along with it.
                if (capture.Sound is { } stream) PlaySound(stream, device.CacheMilliseconds, mute);
                return;
            }
            var video = (VideoSource)input;
            var layout = (video.Layout ?? new Placement()).Normalize();
            // An unmeasured file gets the monitor, which is what every version before this one did.
            var frame = video.Frame ?? screen.Bounds.Size;
            var window = layout.Fit(frame, screen.Bounds.Size);
            var placed = window;
            placed.Offset(screen.Bounds.Location);
            host.Attach(screen, placed);
            player = new MediaPlayer(engine) { Hwnd = host.Handle, Mute = mute };
            player.EnableKeyInput = false;
            player.EnableMouseInput = false;
            // The capture path cuts the bars off in ffmpeg; here VLC does it, which costs nothing
            // because the decoder was going to hand the vout a whole frame either way. It is written
            // as four borders to cut away rather than as "WxH+X+Y", which VLC reads as edges rather
            // than as an origin and a size: asked for 1440 wide at x=240 it gave back 1200.
            var cut = layout.Crop(frame);
            player.CropGeometry = cut.Size == frame ? null
                : $"{cut.X}+{cut.Y}+{frame.Width - cut.Right}+{frame.Height - cut.Bottom}";
            // Anything VLC has left over after fitting the picture to the window it pads, and that
            // padding is not drawn through the swap chain, so it comes out as a hole with the real
            // wallpaper behind it. Declaring the window's own shape as the display aspect leaves
            // nothing to pad: the two stretch modes distort to fill, and the rest are already this
            // shape bar the pixel that rounding the placement cost them.
            //
            // The correction is for VLC working the display aspect out from the whole decoded frame
            // rather than from the part the crop left: asking for the window's ratio outright would
            // squeeze the picture by exactly the ratio between the two, which shows up as a
            // pillarbox of bare wallpaper. Where nothing is cropped the frame terms cancel and this
            // is simply the window's own ratio.
            var wide = (long)window.Width * cut.Height * frame.Width;
            var high = (long)window.Height * cut.Width * frame.Height;
            var factor = Gcd(wide, high);
            player.AspectRatio = Ratio(wide / factor, high / factor);
            player.Playing += (_, _) => Post(current, () => Status?.Invoke(
                $"Playing on the desktop · {frame.Width} × {frame.Height} drawn {window.Width} × {window.Height} at ({window.X}, {window.Y})"));
            player.EncounteredError += (_, _) => Post(current, () =>
            {
                var detail = string.Join(Environment.NewLine, errors);
                Stop();
                Status?.Invoke("Playback failed: " + (string.IsNullOrWhiteSpace(detail) ? "Could not open the input. Check the device connection and its signal." : detail));
            });
            // The media repeats itself, so this is only reached once the repeat count runs out.
            player.EndReached += (_, _) => Post(current, () =>
            {
                if (source?.Loop == true) { player!.Stop(); Play(); }
                else { Stop(); Status?.Invoke("The input signal ended. Check the device and apply again."); }
            });
            Play();
        }
        catch { Stop(); throw; }
    }

    // Sound arrives as WAV on the engine's stdout, so the player reads the stream rather than a file.
    // Caching follows the capture buffer: the same dial that decides how much latency to trade for a
    // steady picture decides it for the sound, and the two paths are not locked to each other anyway.
    private void PlaySound(Stream stream, int cacheMilliseconds, bool mute)
    {
        var media = new Media(engine, new StreamMediaInput(stream),
            ":no-video", ":demux=wav", ":file-caching=" + Math.Max(100, cacheMilliseconds));
        try
        {
            sound = new MediaPlayer(engine) { Mute = mute };
            sound.EnableKeyInput = false;
            sound.EnableMouseInput = false;
            sound.Play(media);
        }
        finally { media.Dispose(); }
    }

    private static long Gcd(long one, long other) => other == 0 ? one : Gcd(other, one % other);

    // VLC multiplies these by a frame dimension in 32 bits, so a ratio that did not reduce to small
    // numbers would overflow there and come back as nonsense. Capped, the rounding is worth well
    // under a pixel across any screen.
    private static string Ratio(long wide, long high)
    {
        const long cap = 100_000;
        var most = Math.Max(wide, high);
        if (most > cap)
        {
            wide = Math.Max(1, (long)Math.Round((double)wide * cap / most));
            high = Math.Max(1, (long)Math.Round((double)high * cap / most));
        }
        return $"{wide}:{high}";
    }

    private void Play()
    {
        using var media = ((VideoSource)source!).Open(engine);
        if (!player!.Play(media)) throw new InvalidOperationException("Could not start playback.");
    }

    private void Post(int current, Action action)
    {
        if (dispatcher.IsDisposed || !dispatcher.IsHandleCreated) return;
        try
        {
            dispatcher.BeginInvoke(() =>
            {
                if (generation != current) return;
                try { action(); }
                catch (Exception ex) { Stop(); Status?.Invoke(ex.Message); }
            });
        }
        catch (InvalidOperationException) { }
    }

    public void SetMute(bool mute)
    {
        if (player is not null) player.Mute = mute;
        if (sound is not null) sound.Mute = mute;
    }
    public void Stop()
    {
        generation++;
        player?.Stop();
        player?.Dispose();
        player = null;
        // The sound player is reading the engine's stdout, so the engine goes first: otherwise the
        // player is asked to stop while parked in a read that nothing is going to answer.
        capture?.Kill();
        sound?.Stop();
        sound?.Dispose();
        sound = null;
        // Tear the window down first: it owns the renderer that reads frames from the capture.
        host?.Dispose();
        host = null;
        capture?.Dispose();
        capture = null;
        source = null;
    }
    public void Dispose() { Stop(); engine.Dispose(); }
}
