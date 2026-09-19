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
                capture.Started += () => Post(current, () => Status?.Invoke($"재생 중 · {options.Format} / {options.ColorSpace} / {options.DynamicRange} / {options.Resolution} / {options.Fps}fps"));
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
            player.Playing += (_, _) => Post(current, () => Status?.Invoke("바탕화면에서 재생 중"));
            player.EncounteredError += (_, _) => Post(current, () =>
            {
                var detail = string.Join(Environment.NewLine, errors);
                Stop();
                Status?.Invoke("재생 실패: " + (string.IsNullOrWhiteSpace(detail) ? "입력을 열 수 없습니다. 장치 연결과 입력 신호를 확인하세요." : detail));
            });
            player.EndReached += (_, _) => Post(current, () =>
            {
                if (source?.Loop == true) { player!.Stop(); Play(); }
                else { Stop(); Status?.Invoke("입력 신호가 종료되었습니다. 장치를 확인하고 다시 적용하세요."); }
            });
            Play();
        }
        catch { Stop(); throw; }
    }

    private void Play()
    {
        using var media = ((VideoSource)source!).Open(engine);
        if (!player!.Play(media)) throw new InvalidOperationException("재생을 시작할 수 없습니다.");
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
