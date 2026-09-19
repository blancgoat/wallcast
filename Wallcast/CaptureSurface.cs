using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Wallcast;

// Explorer's desktop window has no GDI redirection surface, so anything painted into it with GDI is
// never composited. Frames reach the screen only through a DXGI flip-model swap chain, which is the
// same path LibVLC uses for video. Only the picture's own rectangle carries a swap chain, so whatever
// the screen is not covered by it stays the wallpaper the user already had.
internal sealed class CaptureSurface : Control
{
    private readonly CapturePlayback capture;
    private readonly Size frame;
    private int queued;
    private bool stopped;
    private ID3D11Device? device;
    private ID3D11DeviceContext? context;
    private IDXGISwapChain1? stream;
    private ID3D11Texture2D? upload;
    private long presented;
    // Frames actually put on screen, which is not the same as frames received from the device.
    public long Presented => Interlocked.Read(ref presented);
    public event Action<string>? Failed;

    public CaptureSurface(CapturePlayback capture)
    {
        this.capture = capture;
        frame = capture.Options.OutputSize;
        SetStyle(ControlStyles.Opaque | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
        Dock = DockStyle.Fill;
        capture.FrameReady += OnFrameReady;
    }

    protected override void OnHandleCreated(EventArgs e) => base.OnHandleCreated(e);

    // Presenting the moment a frame lands beats polling on a timer, whose ~15ms resolution adds that
    // much latency to every frame. One draw in flight is enough: the capture keeps only the newest.
    private void OnFrameReady()
    {
        if (stopped || Interlocked.Exchange(ref queued, 1) == 1) return;
        try
        {
            if (IsDisposed || !IsHandleCreated) Interlocked.Exchange(ref queued, 0);
            else BeginInvoke(Draw);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            Interlocked.Exchange(ref queued, 0);
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e) { }

    private void CreateResources()
    {
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 },
            out ID3D11Device created, out ID3D11DeviceContext createdContext).CheckError();
        device = created;
        context = createdContext;
        using var dxgi = device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgi.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();
        stream = factory.CreateSwapChainForHwnd(device, Handle, Describe((uint)frame.Width, (uint)frame.Height));
        upload = device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)frame.Width, Height = (uint)frame.Height, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Dynamic, BindFlags = BindFlags.ShaderResource, CPUAccessFlags = CpuAccessFlags.Write
        });
    }

    private static SwapChainDescription1 Describe(uint width, uint height) => new()
    {
        Width = width, Height = height, Format = Format.B8G8R8A8_UNorm,
        BufferCount = 2, BufferUsage = Usage.RenderTargetOutput,
        SwapEffect = SwapEffect.FlipDiscard, Scaling = Scaling.Stretch,
        AlphaMode = AlphaMode.Ignore, SampleDescription = new SampleDescription(1, 0)
    };

    // The swap chains are bound to their windows, so they are built only once the layout has given
    // those windows their real size; a chain created for a zero-sized window never composites.
    private bool Ready()
    {
        if (device is not null) return true;
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return false;
        try { CreateResources(); return true; }
        catch (Exception ex)
        {
            Stop();
            Release();
            Failed?.Invoke("Could not start the desktop renderer: " + ex.Message);
            return false;
        }
    }

    private void Draw()
    {
        Interlocked.Exchange(ref queued, 0);
        if (stopped || !Ready()) return;
        var bytes = capture.TakeFrame();
        if (bytes is null) return;
        try
        {
            if (stream is null || context is null || upload is null) return;
            var row = frame.Width * 4;
            var map = context.Map(upload, 0, MapMode.WriteDiscard);
            try
            {
                // One copy instead of 2160 of them whenever the driver hands back a packed surface.
                if (map.RowPitch == row) Marshal.Copy(bytes, 0, map.DataPointer, row * frame.Height);
                else
                    for (var y = 0; y < frame.Height; y++)
                        Marshal.Copy(bytes, y * row, IntPtr.Add(map.DataPointer, y * (int)map.RowPitch), row);
            }
            finally { context.Unmap(upload, 0); }
            using (var back = stream.GetBuffer<ID3D11Texture2D>(0)) context.CopyResource(back, upload);
            stream.Present(0, PresentFlags.None);
            Interlocked.Increment(ref presented);
        }
        catch (Exception ex)
        {
            Stop();
            Failed?.Invoke("Desktop rendering stopped: " + ex.Message);
        }
        finally { CapturePlayback.ReturnFrame(bytes); }
    }

    private void Stop()
    {
        stopped = true;
        capture.FrameReady -= OnFrameReady;
    }

    private void Release()
    {
        Stop();
        upload?.Dispose(); upload = null;
        stream?.Dispose(); stream = null;
        context?.Dispose(); context = null;
        device?.Dispose(); device = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Release();
        base.Dispose(disposing);
    }
}
