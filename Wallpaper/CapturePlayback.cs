using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Still;

// DirectShow -> explicitly selected YUV conversion -> BGRA. No encoding or disk recording.
internal sealed class CapturePlayback : IDisposable
{
    private readonly Process process;
    private readonly CancellationTokenSource cancellation = new();
    private readonly ConcurrentQueue<string> errors = new();
    private Task reader = Task.CompletedTask;
    private byte[]? latest;
    private long frames;
    public long Frames => Interlocked.Read(ref frames);
    public event Action<string>? Failed;
    public event Action? Started;
    public CaptureOptions Options { get; }
    public string? InputDescription { get; private set; }

    public CapturePlayback(string device, int buffer, CaptureOptions options)
    {
        Options = options;
        var info = options.CreateStartInfo(device, buffer);
        if (!File.Exists(info.FileName)) throw new FileNotFoundException("캡처 엔진이 없습니다. Still 폴더 전체를 다시 복사해 주세요.", info.FileName);
        process = new Process { StartInfo = info };
        process.ErrorDataReceived += (sender, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            if (InputDescription is null && e.Data.Contains("Video:")) InputDescription = e.Data.Trim();
            errors.Enqueue(e.Data);
            while (errors.Count > 12) errors.TryDequeue(out _);
        };
        try
        {
            process.Start();
            process.BeginErrorReadLine();
        }
        catch { process.Dispose(); cancellation.Dispose(); throw; }
    }

    public void Start() => reader = Task.Run(ReadFrames);

    private async Task ReadFrames()
    {
        var token = cancellation.Token;
        var length = checked(Options.FrameSize.Width * Options.FrameSize.Height * 4);
        try
        {
            while (!token.IsCancellationRequested)
            {
                byte[]? frame = ArrayPool<byte>.Shared.Rent(length);
                try
                {
                    // Also detects a disconnected/stalled capture device instead of hanging forever.
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(15));
                    await process.StandardOutput.BaseStream.ReadExactlyAsync(frame.AsMemory(0, length), timeout.Token);
                    var old = Interlocked.Exchange(ref latest, frame);
                    frame = null;
                    if (old is not null) ArrayPool<byte>.Shared.Return(old);
                    if (Interlocked.Increment(ref frames) == 1) Started?.Invoke();
                }
                finally { if (frame is not null) ArrayPool<byte>.Shared.Return(frame); }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            if (!token.IsCancellationRequested)
            {
                try { if (!process.HasExited) process.Kill(true); process.WaitForExit(2000); } catch (InvalidOperationException) { }
                Failed?.Invoke("캡처 입력을 받지 못했습니다. 장치가 선택한 형식·해상도·FPS를 지원하는지 확인하세요.\n" + string.Join("\n", errors.TakeLast(5)));
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
        process.Dispose();
        cancellation.Dispose();
    }
}
