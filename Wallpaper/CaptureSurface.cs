using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Still;

// Explorer's desktop window has no GDI redirection surface, so anything painted into it with GDI is
// never composited. Frames reach the screen only through a DXGI flip-model swap chain, which is the
// same path LibVLC uses for video. One chain fills the letterbox with black, one carries the frames.
internal sealed class CaptureSurface : Control
{
    private readonly CapturePlayback capture;
    private readonly Layer video = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 15 };
    private readonly Size frame;
    private ID3D11Device? device;
    private ID3D11DeviceContext? context;
    private IDXGISwapChain1? backdrop;
    private IDXGISwapChain1? stream;
    private ID3D11Texture2D? upload;
    public event Action<string>? Failed;

    public CaptureSurface(CapturePlayback capture)
    {
        this.capture = capture;
        frame = capture.Options.FrameSize;
        SetStyle(ControlStyles.Opaque | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
        Dock = DockStyle.Fill;
        BackColor = Color.Black;
        Controls.Add(video);
        timer.Tick += (_, _) => Draw();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Arrange();
        timer.Start();
    }

    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); Arrange(); }
    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e) { }
    private void Arrange()
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        video.Bounds = capture.Options.Fit(ClientSize);
        if (backdrop is not null) Fill(backdrop, Black);
    }

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
        // A 1x1 chain is enough for the bars: the swap chain is stretched to the whole control.
        backdrop = factory.CreateSwapChainForHwnd(device, Handle, Describe(1, 1));
        stream = factory.CreateSwapChainForHwnd(device, video.Handle, Describe((uint)frame.Width, (uint)frame.Height));
        upload = device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)frame.Width, Height = (uint)frame.Height, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Dynamic, BindFlags = BindFlags.ShaderResource, CPUAccessFlags = CpuAccessFlags.Write
        });
        Fill(backdrop, Black);
    }

    private static SwapChainDescription1 Describe(uint width, uint height) => new()
    {
        Width = width, Height = height, Format = Format.B8G8R8A8_UNorm,
        BufferCount = 2, BufferUsage = Usage.RenderTargetOutput,
        SwapEffect = SwapEffect.FlipDiscard, Scaling = Scaling.Stretch,
        AlphaMode = AlphaMode.Ignore, SampleDescription = new SampleDescription(1, 0)
    };

    private static readonly Vortice.Mathematics.Color4 Black = new(0f, 0f, 0f, 1f);
    private void Fill(IDXGISwapChain1 chain, Vortice.Mathematics.Color4 colour)
    {
        using var back = chain.GetBuffer<ID3D11Texture2D>(0);
        using var view = device!.CreateRenderTargetView(back);
        context!.ClearRenderTargetView(view, colour);
        chain.Present(0, PresentFlags.None);
    }

    // The swap chains are bound to their windows, so they are built only once the layout has given
    // those windows their real size; a chain created for a zero-sized window never composites.
    private bool Ready()
    {
        if (device is not null) return true;
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0 || video.Width <= 0 || video.Height <= 0) return false;
        try { CreateResources(); return true; }
        catch (Exception ex)
        {
            timer.Stop();
            Release();
            Failed?.Invoke("바탕화면 렌더러를 초기화하지 못했습니다: " + ex.Message);
            return false;
        }
    }

    private void Draw()
    {
        if (!Ready()) return;
        var bytes = capture.TakeFrame();
        if (bytes is null) return;
        try
        {
            if (stream is null || context is null || upload is null) return;
            var row = frame.Width * 4;
            var map = context.Map(upload, 0, MapMode.WriteDiscard);
            try
            {
                for (var y = 0; y < frame.Height; y++)
                    Marshal.Copy(bytes, y * row, IntPtr.Add(map.DataPointer, y * (int)map.RowPitch), row);
            }
            finally { context.Unmap(upload, 0); }
            using (var back = stream.GetBuffer<ID3D11Texture2D>(0)) context.CopyResource(back, upload);
            stream.Present(0, PresentFlags.None);
        }
        catch (Exception ex)
        {
            timer.Stop();
            Failed?.Invoke("바탕화면 렌더링이 중단되었습니다: " + ex.Message);
        }
        finally { CapturePlayback.ReturnFrame(bytes); }
    }

    private void Release()
    {
        timer.Stop();
        upload?.Dispose(); upload = null;
        stream?.Dispose(); stream = null;
        backdrop?.Dispose(); backdrop = null;
        context?.Dispose(); context = null;
        device?.Dispose(); device = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { Release(); timer.Dispose(); }
        base.Dispose(disposing);
    }

    // The swap chain owns these pixels; letting WinForms paint over them only wastes time.
    private sealed class Layer : Control
    {
        public Layer() => SetStyle(ControlStyles.Opaque | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e) { }
    }
}
