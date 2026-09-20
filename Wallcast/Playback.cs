using LibVLCSharp.Shared;
using System.Collections.Concurrent;

namespace Wallcast;

internal sealed class Playback : IDisposable
{
    private readonly Control dispatcher;
    private readonly LibVLC engine;
    private MediaPlayer? player;
    private CapturePlayback? capture;
    private CaptureSurface? surface;
    private MediaPlayer? sound;
    private SoundStream? heard;
    private System.Threading.Timer? drift, waiting, listen;
    private CaptureSource? live;
    private bool liveMute;
    private long driftBytes, driftAt;
    private int offBand, reopens, disagreed;
    private bool reopening, deaf;
    // What the device says it is receiving, which is not the same as what the pin was opened at.
    public int SoundOffered { get; private set; }
    private DesktopHost? host;
    private IWallpaperSource? source;
    private int generation;
    private readonly ConcurrentQueue<string> errors = new();
    public event Action<string>? Status;
    // What the sound is actually arriving at, as opposed to the rate the pin claims. They part company
    // when the source changes rate under a pin that has already negotiated one.
    public double SoundArriving { get; private set; }
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
                live = device with { Options = options };
                liveMute = mute;
                reopens = 0;
                deaf = false;
                OpenCapture(current, screen, true);
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

    // Everything from the engine down, kept apart from Start because the sound following a source that
    // changed its rate means doing it again. The renderer is built once and handed the new capture, so
    // what the user sees of a re-open is the last frame held for about a second.
    private void OpenCapture(int current, Screen screen, bool first)
    {
        var device = live!;
        var options = device.Options!;
        var engine = new CapturePlayback(device.Device, device.CacheMilliseconds, options);
        capture = engine;
        engine.Started += () => Post(current, () => Status?.Invoke($"Playing · {options.Format} / {options.ColorSpace} / {options.DynamicRange} / {options.Resolution} / {options.Fps}fps"
            + (options.HasSound ? " · sound " + (capture?.SoundDescription ?? "from " + options.Audio) : "")));
        engine.Failed += detail => Post(current, () =>
        {
            // An engine that has already been replaced has nothing left to report. Its pipe broke
            // because this ended it, which is not a reason to take the picture down.
            if (!ReferenceEquals(engine, capture)) return;
            if (engine.Frames == 0 && options.HasSound)
            {
                // A re-open races the engine it just ended for the device, and losing that race says
                // nothing about whether the device has sound. Only a device that would not give any
                // in the first place is written off, and even then the picture is kept.
                if (!first && reopens < 3) { Reopen(current, screen); return; }
                Start(device with { Options = options with { Audio = "" } }, screen, liveMute);
                Status?.Invoke(first
                    ? $"{options.Audio} gave no sound, so the picture is playing without it."
                    : $"{options.Audio} would not re-open for sound, so the picture is going on without it.");
                return;
            }
            Stop();
            Status?.Invoke(detail);
        });
        if (surface is null)
        {
            surface = new CaptureSurface(engine);
            surface.Failed += detail => Post(current, () => { Stop(); Status?.Invoke(detail); });
            host!.Controls.Add(surface);
        }
        else surface.Follow(engine);
        engine.Start();
        // Started before the picture is asked for, so nothing is left filling the engine's stdout
        // while waiting: a full pipe there would stall the picture along with it.
        reopening = false;
        if (engine.Sound is not { } stream) return;
        reopens = 0;
        heard = new SoundStream(stream);
        PlaySound(heard, device.CacheMilliseconds, liveMute);
        driftBytes = 0;
        driftAt = Environment.TickCount64;
        offBand = 0;
        var opened = driftAt;
        drift = new System.Threading.Timer(_ => Post(current, () => CheckDrift(current, screen, opened)), null, 1000, 1000);
        disagreed = 0;
        // On the UI thread on purpose: the answer takes about eight milliseconds and the objects it
        // touches belong to this apartment. Two seconds apart, and only while there is sound to keep
        // right - a muted capture has nothing to follow.
        if (!deaf) listen = new System.Threading.Timer(_ => Post(current, () => CheckOffered(current, screen)), null, 2000, 2000);
    }

    // The other half of the problem, and the half that cannot be inferred from the sound itself. A card
    // that resamples to the rate its pin was opened at keeps sending exactly as many bytes a second as
    // that rate implies, however badly it mangles them on the way. Only the device knows, and it says
    // so by which format it puts at the head of its list, which it will answer even mid-capture.
    //
    // A device that will not answer is not asked again: the sound then simply keeps whatever it
    // negotiated when playback started, which is what it did before any of this existed.
    private void CheckOffered(int current, Screen screen)
    {
        if (capture is null || live is null || listen is null || reopening) return;
        if (capture.SoundRate <= 0) return;
        List<int> offered;
        try { offered = SoundFormats.Rates(live.Device); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException) { offered = []; }
        if (offered.Count == 0)
        {
            deaf = true;
            listen.Dispose();
            listen = null;
            return;
        }
        SoundOffered = offered[0];
        disagreed = offered[0] != capture.SoundRate ? disagreed + 1 : 0;
        // Twice, because a track boundary can leave the device between rates for a moment.
        if (disagreed < 2) return;
        Status?.Invoke($"The source is now {offered[0]} Hz and the sound was opened at {capture.SoundRate}; re-opening.");
        Reopen(current, screen);
    }

    // A pin keeps the rate it negotiated for as long as it is open, so a source that switches from one
    // track to the next at a different rate says nothing: it just starts arriving at a different speed
    // under the old label, and plays fast or slow by the ratio between them. Counting what arrives is
    // the whole of the detection - there is nothing to poll and nothing to ask the device.
    //
    // Three seconds of disagreement rather than one, because a second of jitter is worth about 1% and
    // the gap between two rates a card offers is never less than 8.8%.
    private void CheckDrift(int current, Screen screen, long opened)
    {
        if (capture is null || heard is null || drift is null) return;
        var now = Environment.TickCount64;
        var bytes = heard.Delivered;
        var since = bytes - driftBytes;
        var elapsed = (now - driftAt) / 1000.0;
        driftBytes = bytes;
        driftAt = now;
        // The first seconds carry the device negotiating and a burst of whatever it had buffered.
        if (now - opened < 3000 || elapsed <= 0 || capture.SoundRate <= 0) { offBand = 0; return; }
        var arriving = since / elapsed / 4;
        SoundArriving = arriving;
        // Six percent, not four: the readings wander by about 2.5% and the smallest real gap between
        // two rates a card offers is 8.8%, so there is room to sit well clear of the noise.
        offBand = Math.Abs(arriving / capture.SoundRate - 1) > 0.06 ? offBand + 1 : 0;
        if (offBand < 3) return;
        Status?.Invoke($"The source moved to about {arriving:F0} Hz from {capture.SoundRate}; re-opening the sound.");
        Reopen(current, screen);
    }

    // Only the engine is replaced. The window, the renderer and its swap chain all stay, which is why
    // this costs a frozen frame rather than a hole in the wallpaper.
    //
    // Not immediately, though. DirectShow does not hand the device straight back, and opening it again
    // in the same breath loses to the engine that is still letting go of it.
    private void Reopen(int current, Screen screen)
    {
        // Both signals can land on the same change, and one re-open is enough.
        if (reopening) return;
        reopening = true;
        disagreed = 0;
        drift?.Dispose();
        drift = null;
        listen?.Dispose();
        listen = null;
        SoundArriving = 0;
        reopens++;
        ReleaseCapture();
        // Held in a field, not a local: a timer nothing refers to can be collected before it fires.
        waiting?.Dispose();
        waiting = new System.Threading.Timer(_ => Post(current, () => OpenCapture(current, screen, false)));
        waiting.Change(400 * reopens, Timeout.Infinite);
    }

    private void ReleaseCapture()
    {
        // The player is reading the engine's stdout, so the engine goes first: otherwise the player is
        // asked to stop while parked in a read that nothing is going to answer.
        capture?.Kill();
        sound?.Stop();
        sound?.Dispose();
        sound = null;
        heard?.Dispose();
        heard = null;
        capture?.Dispose();
        capture = null;
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
        drift?.Dispose();
        drift = null;
        listen?.Dispose();
        listen = null;
        waiting?.Dispose();
        waiting = null;
        reopening = false;
        SoundOffered = 0;
        player?.Stop();
        player?.Dispose();
        player = null;
        capture?.Kill();
        sound?.Stop();
        sound?.Dispose();
        sound = null;
        heard?.Dispose();
        heard = null;
        // Tear the window down before the capture: it owns the renderer that reads the frames.
        host?.Dispose();
        host = null;
        surface = null;
        capture?.Dispose();
        capture = null;
        live = null;
        source = null;
    }
    public void Dispose() { Stop(); engine.Dispose(); }
}
