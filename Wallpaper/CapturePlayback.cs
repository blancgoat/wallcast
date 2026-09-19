using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;

namespace Still;

// DirectShow -> explicitly selected YUV conversion -> BGRA. No encoding or disk recording.
internal sealed class CapturePlayback : IDisposable
{
    // A redirected stdout pipe is created with a small fixed buffer and tops out near 800 MB/s, which
    // is not enough for 4K BGRA at 60fps (2.0 GB/s). Frames then queue up in the DirectShow buffer and
    // arrive seconds late. A named pipe carries the same stream at roughly twice the rate.
    private const int PipeBuffer = 4 << 20;
    private readonly Process process;
    private readonly NamedPipeServerStream pipe;
    private readonly CancellationTokenSource cancellation = new();
    private readonly CancellationTokenSource startup = new();
    private readonly ConcurrentQueue<string> errors = new();
    private Task reader = Task.CompletedTask;
    private byte[]? latest;
    private long frames;
    public long Frames => Interlocked.Read(ref frames);
    public event Action<string>? Failed;
    public event Action? Started;
    // Raised on the reader thread as soon as a frame lands, so the renderer does not have to poll.
    public event Action? FrameReady;
    public CaptureOptions Options { get; }
    public string? InputDescription { get; private set; }

    public CapturePlayback(string device, int buffer, CaptureOptions options)
    {
        Options = options;
        var name = "still-" + Guid.NewGuid().ToString("N");
        var info = options.CreateStartInfo(device, buffer, @"\\.\pipe\" + name);
        if (!File.Exists(info.FileName)) throw new FileNotFoundException("The capture engine is missing. Copy the whole Still folder again.", info.FileName);
        pipe = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, PipeBuffer, PipeBuffer);
        process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.ErrorDataReceived += (sender, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            if (InputDescription is null && e.Data.Contains("Video:")) InputDescription = e.Data.Trim();
            errors.Enqueue(e.Data);
            while (errors.Count > 12) errors.TryDequeue(out _);
        };
        // Stop waiting for the stream the moment the engine gives up on the selected settings.
        process.Exited += (sender, e) => { try { startup.Cancel(); } catch (ObjectDisposedException) { } };
        try
        {
            process.Start();
            process.BeginErrorReadLine();
        }
        catch { pipe.Dispose(); process.Dispose(); cancellation.Dispose(); startup.Dispose(); throw; }
    }

    public void Start() => reader = Task.Run(ReadFrames);

    private async Task ReadFrames()
    {
        var token = cancellation.Token;
        var length = checked(Options.FrameSize.Width * Options.FrameSize.Height * 4);
        try
        {
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(token, startup.Token))
            {
                connect.CancelAfter(TimeSpan.FromSeconds(15));
                await pipe.WaitForConnectionAsync(connect.Token);
            }
            while (!token.IsCancellationRequested)
            {
                byte[]? frame = ArrayPool<byte>.Shared.Rent(length);
                try
                {
                    // Also detects a disconnected/stalled capture device instead of hanging forever.
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(15));
                    await pipe.ReadExactlyAsync(frame.AsMemory(0, length), timeout.Token);
                    var old = Interlocked.Exchange(ref latest, frame);
                    frame = null;
                    if (old is not null) ArrayPool<byte>.Shared.Return(old);
                    if (Interlocked.Increment(ref frames) == 1) Started?.Invoke();
                    FrameReady?.Invoke();
                }
                finally { if (frame is not null) ArrayPool<byte>.Shared.Return(frame); }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            if (!token.IsCancellationRequested)
            {
                try { if (!process.HasExited) process.Kill(true); process.WaitForExit(2000); } catch (InvalidOperationException) { }
                Failed?.Invoke("No capture input. Check that the device supports the selected format, resolution and frame rate.\n" + string.Join("\n", errors.TakeLast(5)));
            }
        }
    }

    public byte[]? TakeFrame() => Interlocked.Exchange(ref latest, null);
    public static void ReturnFrame(byte[] frame) => ArrayPool<byte>.Shared.Return(frame);
    public void Dispose()
    {
        cancellation.Cancel();
        try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
        // The reader never waits on the UI thread, so this cannot deadlock UI event callbacks.
        reader.GetAwaiter().GetResult();
        var frame = TakeFrame();
        if (frame is not null) ReturnFrame(frame);
        pipe.Dispose();
        process.Dispose();
        cancellation.Dispose();
        startup.Dispose();
    }
}
