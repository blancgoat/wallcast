namespace Wallcast;

// Sits between the capture engine's stdout and the player, reading as fast as the engine writes.
//
// Two things come of that. The engine never waits on a player that is pacing itself, which matters
// because it is the same process that is sending the picture. And because the reading is not paced,
// the byte count is what the device is really sending rather than what the player felt like taking,
// which is the only way to notice that a source has changed its sample rate: the pin keeps the rate it
// negotiated, so a 44.1kHz track arriving on a pin opened at 48000 announces itself by arriving 8.8%
// slower and nothing else.
internal sealed class SoundStream : Stream
{
    // About five seconds of stereo 48kHz. Nothing should ever come near it - the player is reading
    // continuously - and if something does, the sound is already wrong and a restart is on its way.
    private const int Cap = 2 << 20, Chunk = 64 << 10;
    private readonly Stream source;
    private readonly Queue<byte[]> waiting = new();
    private readonly Queue<int> sizes = new();
    private readonly object gate = new();
    private readonly Task pump;
    private int head, buffered;
    private bool finished, closed;
    private long delivered;

    // Bytes the engine has handed over since it started, which is the measurement this exists for.
    public long Delivered => Interlocked.Read(ref delivered);

    public SoundStream(Stream source)
    {
        this.source = source;
        pump = Task.Run(Pump);
    }

    private void Pump()
    {
        try
        {
            while (true)
            {
                var chunk = new byte[Chunk];
                var read = source.Read(chunk, 0, Chunk);
                if (read <= 0) break;
                Interlocked.Add(ref delivered, read);
                lock (gate)
                {
                    if (closed) break;
                    // Dropping the oldest beats holding the engine up. By the time this bites the
                    // sound is already wrong, and a silent gap is the least of what is happening.
                    while (buffered + read > Cap && waiting.Count > 0)
                    {
                        buffered -= sizes.Dequeue() - head;
                        waiting.Dequeue();
                        head = 0;
                    }
                    waiting.Enqueue(chunk);
                    sizes.Enqueue(read);
                    buffered += read;
                    Monitor.PulseAll(gate);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        finally
        {
            lock (gate) { finished = true; Monitor.PulseAll(gate); }
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        lock (gate)
        {
            while (waiting.Count == 0 && !finished && !closed) Monitor.Wait(gate);
            if (waiting.Count == 0) return 0;
            var chunk = waiting.Peek();
            var size = sizes.Peek();
            var take = Math.Min(count, size - head);
            Buffer.BlockCopy(chunk, head, buffer, offset, take);
            head += take;
            buffered -= take;
            if (head >= size) { waiting.Dequeue(); sizes.Dequeue(); head = 0; }
            return take;
        }
    }

    protected override void Dispose(bool disposing)
    {
        lock (gate) { closed = true; Monitor.PulseAll(gate); }
        if (disposing) { try { pump.Wait(2000); } catch (AggregateException) { } }
        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
