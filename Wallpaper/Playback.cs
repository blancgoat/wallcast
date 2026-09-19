using LibVLCSharp.Shared;
using System.Collections.Concurrent;

namespace Still;

internal sealed class Playback : IDisposable
{
    private readonly Control dispatcher;
    private readonly LibVLC engine;
    private MediaPlayer? player;
    private CapturePlayback? capture;
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
            host.Attach(screen);
            if (input is CaptureSource device)
            {
                var options = (device.Options ?? new CaptureOptions()).Normalize();
                capture = new CapturePlayback(device.Device, device.CacheMilliseconds, options);
                capture.Started += () => Post(current, () => Status?.Invoke($"Playing · {options.Format} / {options.ColorSpace} / {options.DynamicRange} / {options.Resolution} / {options.Fps}fps"));
                capture.Failed += detail => Post(current, () => { Stop(); Status?.Invoke(detail); });
                var surface = new CaptureSurface(capture);
                surface.Failed += detail => Post(current, () => { Stop(); Status?.Invoke(detail); });
                host.Controls.Add(surface);
                capture.Start();
                return;
            }
            player = new MediaPlayer(engine) { Hwnd = host.Handle, Mute = mute };
            player.EnableKeyInput = false;
            player.EnableMouseInput = false;
            player.Playing += (_, _) => Post(current, () => Status?.Invoke("Playing on the desktop"));
            player.EncounteredError += (_, _) => Post(current, () =>
            {
                var detail = string.Join(Environment.NewLine, errors);
                Stop();
                Status?.Invoke("Playback failed: " + (string.IsNullOrWhiteSpace(detail) ? "Could not open the input. Check the device connection and its signal." : detail));
            });
            player.EndReached += (_, _) => Post(current, () =>
            {
                if (source?.Loop == true) { player!.Stop(); Play(); }
                else { Stop(); Status?.Invoke("The input signal ended. Check the device and apply again."); }
            });
            Play();
        }
        catch { Stop(); throw; }
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

    public void SetMute(bool mute) { if (player is not null) player.Mute = mute; }
    public void Stop()
    {
        generation++;
        player?.Stop();
        player?.Dispose();
        player = null;
        // Tear the window down first: it owns the renderer that reads frames from the capture.
        host?.Dispose();
        host = null;
        capture?.Dispose();
        capture = null;
        source = null;
    }
    public void Dispose() { Stop(); engine.Dispose(); }
}
