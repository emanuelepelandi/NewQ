using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using NewQ.App.Infrastructure;
using NewQ.Core.Video;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace NewQ.App.Video.Gpu;

/// <summary>
/// Renders one screen (monitor or preview window) on its own thread, locked to that screen's refresh with a
/// flip-model swap chain: for each route output on the screen, the route canvas is composed (layers with
/// opacity, test patterns), then drawn through the output's mesh (crop, placement, keystone, warp) with edge
/// blending. Each renderer has its own D3D11 device, so screens never wait for each other.
/// </summary>
internal sealed class GpuRenderer : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Vertex
    {
        public Vector2 Position;
        public Vector2 Uv;
        public Vector2 Local;
        public Vertex(float x, float y, float u, float v, float lu, float lv)
        {
            Position = new Vector2(x, y);
            Uv = new Vector2(u, v);
            Local = new Vector2(lu, lv);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Params
    {
        public Vector4 P0;          // opacity, pattern kind, time, frame
        public Vector4 P1;          // canvas w, canvas h, output index, identify
        public Vector4 Blend;       // left, right, top, bottom
        public Vector4 BlendShape;  // gamma, curve
    }

    private sealed class CachedTexture : IDisposable
    {
        public ID3D11Texture2D Texture = null!;
        public ID3D11ShaderResourceView View = null!;
        public int Width, Height;
        public long Frame = -1;
        public long LastUsed;
        public void Dispose() { View.Dispose(); Texture.Dispose(); }
    }

    private sealed class Canvas : IDisposable
    {
        public ID3D11Texture2D Texture = null!;
        public ID3D11RenderTargetView Target = null!;
        public ID3D11ShaderResourceView View = null!;
        public int Width, Height;
        public long LastUsed;
        public void Dispose() { View.Dispose(); Target.Dispose(); Texture.Dispose(); }
    }

    private static readonly int VertexSize = Marshal.SizeOf<Vertex>();

    private readonly IntPtr _hwnd;
    private readonly int _screenIndex;
    private readonly CompositionHub _hub;
    private readonly Thread _thread;
    private volatile bool _stop;

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGISwapChain1? _swapChain;
    private ID3D11RenderTargetView? _backBuffer;
    private ID3D11VertexShader? _vs;
    private ID3D11PixelShader? _psLayer, _psPattern, _psOutput;
    private ID3D11InputLayout? _layout;
    private ID3D11Buffer? _constants;
    private ID3D11Buffer? _vertices;
    private int _vertexCapacity;
    private ID3D11SamplerState? _sampler;
    private ID3D11BlendState? _premultiplied, _opaque;
    private ID3D11RasterizerState? _noCull;
    private readonly Dictionary<IFrameSource, CachedTexture> _textures = new();
    private readonly Dictionary<Guid, Canvas> _canvases = new();
    private int _width, _height;
    private long _frame;

    public GpuRenderer(IntPtr hwnd, int screenIndex, CompositionHub hub)
    {
        _hwnd = hwnd;
        _screenIndex = screenIndex;
        _hub = hub;
        _thread = new Thread(Run) { IsBackground = true, Name = $"NewQ render {screenIndex}", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>Raised on the render thread if the renderer stops because of an error.</summary>
    public event Action<string>? Failed;

    /// <summary>Frames presented (diagnostics, stress tests).</summary>
    public long FramesPresented => Interlocked.Read(ref _frame);

    // ------------------------------------------------------------------ thread

    private void Run()
    {
        var failures = 0;
        while (!_stop)
        {
            try
            {
                if (_device is null) CreateDevice();
                RenderFrame();
                failures = 0;
            }
            catch (Exception ex) when (!_stop)
            {
                // Device lost (driver update, GPU reset, display change): rebuild everything and carry on.
                ReleaseDevice();
                if (++failures >= 5)
                {
                    Failed?.Invoke($"Uscita video {(_screenIndex < 0 ? "anteprima" : $"schermo {_screenIndex + 1}")}: {ex.Message}");
                    Thread.Sleep(1000);
                    failures = 0;
                }
                else Thread.Sleep(50);
            }
        }
        ReleaseDevice();
    }

    // ------------------------------------------------------------------ device

    private void CreateDevice()
    {
        var levels = new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 };
        if (D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels, out var device, out var context).Failure)
            D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Warp, DeviceCreationFlags.BgraSupport, levels, out device, out context).CheckError();
        _device = device!;
        _context = context!;

        var vsCode = Compile("VSMain", "vs_4_0");
        _vs = _device.CreateVertexShader(vsCode);
        _psLayer = _device.CreatePixelShader(Compile("PSLayer", "ps_4_0"));
        _psPattern = _device.CreatePixelShader(Compile("PSPattern", "ps_4_0"));
        _psOutput = _device.CreatePixelShader(Compile("PSOutput", "ps_4_0"));
        _layout = _device.CreateInputLayout(new[]
        {
            new InputElementDescription("POSITION", 0, Format.R32G32_Float, 0, 0),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 8, 0),
            new InputElementDescription("TEXCOORD", 1, Format.R32G32_Float, 16, 0),
        }, vsCode);
        _constants = _device.CreateBuffer(new BufferDescription(Marshal.SizeOf<Params>(), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write, ResourceOptionFlags.None, 0));
        _sampler = _device.CreateSamplerState(SamplerDescription.LinearClamp);
        _premultiplied = _device.CreateBlendState(BlendDescription.AlphaBlend);
        _opaque = _device.CreateBlendState(BlendDescription.Opaque);
        _noCull = _device.CreateRasterizerState(RasterizerDescription.CullNone);

        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory2>();
        var (w, h) = ClientSize();
        _swapChain = factory.CreateSwapChainForHwnd(_device, _hwnd, new SwapChainDescription1
        {
            Width = w,
            Height = h,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = AlphaMode.Ignore,
        }, null, null);
        factory.MakeWindowAssociation(_hwnd, WindowAssociationFlags.IgnoreAll); // no Alt+Enter fullscreen switching
        CreateBackBuffer(w, h);
    }

    private static byte[] Compile(string entry, string profile)
    {
        var result = Compiler.Compile(Shaders.Source, entry, "NewQ.hlsl", profile, out var blob, out var errors);
        if (result.Failure || blob is null)
            throw new InvalidOperationException($"Shader {entry}: {(errors?.AsBytes() is byte[] e ? System.Text.Encoding.ASCII.GetString(e) : result.ToString())}");
        var bytes = blob.AsBytes();
        blob.Dispose();
        errors?.Dispose();
        return bytes;
    }

    private void CreateBackBuffer(int w, int h)
    {
        using var buffer = _swapChain!.GetBuffer<ID3D11Texture2D>(0);
        _backBuffer = _device!.CreateRenderTargetView(buffer);
        _width = w;
        _height = h;
    }

    private (int W, int H) ClientSize()
    {
        NativeMethods.GetClientRect(_hwnd, out var r);
        return (Math.Max(1, r.Right - r.Left), Math.Max(1, r.Bottom - r.Top));
    }

    private void ReleaseDevice()
    {
        foreach (var t in _textures.Values) t.Dispose();
        _textures.Clear();
        foreach (var c in _canvases.Values) c.Dispose();
        _canvases.Clear();
        _backBuffer?.Dispose(); _backBuffer = null;
        _swapChain?.Dispose(); _swapChain = null;
        _vertices?.Dispose(); _vertices = null; _vertexCapacity = 0;
        _constants?.Dispose(); _constants = null;
        _layout?.Dispose(); _layout = null;
        _vs?.Dispose(); _vs = null;
        _psLayer?.Dispose(); _psLayer = null;
        _psPattern?.Dispose(); _psPattern = null;
        _psOutput?.Dispose(); _psOutput = null;
        _sampler?.Dispose(); _sampler = null;
        _premultiplied?.Dispose(); _premultiplied = null;
        _opaque?.Dispose(); _opaque = null;
        _noCull?.Dispose(); _noCull = null;
        _context?.ClearState();
        _context?.Dispose(); _context = null;
        _device?.Dispose(); _device = null;
    }

    // ------------------------------------------------------------------ frame

    private void RenderFrame()
    {
        var ctx = _context!;
        var (w, h) = ClientSize();
        if (w != _width || h != _height)
        {
            _backBuffer?.Dispose();
            _swapChain!.ResizeBuffers(2, w, h, Format.B8G8R8A8_UNorm, SwapChainFlags.None).CheckError();
            CreateBackBuffer(w, h);
        }

        var now = _hub.Now;
        var frame = Interlocked.Increment(ref _frame);
        var work = _hub.SnapshotFor(_screenIndex);

        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.IASetInputLayout(_layout);
        ctx.VSSetShader(_vs);
        ctx.RSSetState(_noCull);
        ctx.PSSetSampler(0, _sampler);
        ctx.PSSetConstantBuffer(0, _constants);

        // 1) Compose each route used on this screen into its canvas (once, even if it has several outputs here).
        foreach (var route in work.Select(x => x.Route).DistinctBy(r => r.RouteId))
            ComposeCanvas(route, now, frame);

        // 2) Draw outputs onto the back buffer.
        ctx.OMSetRenderTargets(_backBuffer!);
        ctx.RSSetViewport(new Viewport(_width, _height));
        ctx.ClearRenderTargetView(_backBuffer!, new Color4(0, 0, 0, 1));
        ctx.OMSetBlendState(_opaque);
        foreach (var (route, output) in work)
            DrawOutput(_canvases[route.RouteId], output);

        _swapChain!.Present(1, PresentFlags.None).CheckError();
        PruneCaches(frame);
    }

    private void ComposeCanvas(RouteSnapshot route, double now, long frame)
    {
        var ctx = _context!;
        if (!_canvases.TryGetValue(route.RouteId, out var canvas) || canvas.Width != route.CanvasWidth || canvas.Height != route.CanvasHeight)
        {
            canvas?.Dispose();
            canvas = CreateCanvas(route.CanvasWidth, route.CanvasHeight);
            _canvases[route.RouteId] = canvas;
        }
        canvas.LastUsed = frame;

        ctx.OMSetRenderTargets(canvas.Target);
        ctx.RSSetViewport(new Viewport(canvas.Width, canvas.Height));
        ctx.ClearRenderTargetView(canvas.Target, new Color4(0, 0, 0, 1));
        ctx.OMSetBlendState(_premultiplied);

        foreach (var layer in route.Layers)
        {
            var opacity = layer.Opacity.ValueAt(now);
            if (opacity <= 0.001) continue;

            if (layer.Pattern is TestPatternKind kind)
            {
                SetParams(new Params
                {
                    P0 = new Vector4((float)opacity, (float)kind, (float)now, frame),
                    P1 = new Vector4(canvas.Width, canvas.Height, 0, 0),
                });
                ctx.PSSetShader(_psPattern);
                DrawQuad(0, 0, 1, 1);
                continue;
            }

            if (layer.Source is not IFrameSource source || GetTexture(source, frame) is not CachedTexture texture) continue;
            var (x, y, qw, qh) = OutputGeometry.Fit(layer.Fit, texture.Width, texture.Height, canvas.Width, canvas.Height);
            SetParams(new Params { P0 = new Vector4((float)opacity, -1, (float)now, frame) });
            ctx.PSSetShader(_psLayer);
            ctx.PSSetShaderResource(0, texture.View);
            DrawQuad(x, y, qw, qh);
        }
        ctx.PSSetShaderResource(0, null!);
    }

    private void DrawOutput(Canvas canvas, OutputSnapshot output)
    {
        var ctx = _context!;
        SetParams(new Params
        {
            P1 = new Vector4(canvas.Width, canvas.Height, output.Index, _hub.IdentifyOutputs ? 1 : 0),
            Blend = new Vector4((float)output.BlendLeft, (float)output.BlendRight, (float)output.BlendTop, (float)output.BlendBottom),
            BlendShape = new Vector4((float)output.BlendGamma, (float)output.BlendCurve, 0, 0),
        });
        ctx.PSSetShader(_psOutput);
        ctx.PSSetShaderResource(0, canvas.View);

        var mesh = output.Mesh;
        var vertices = new Vertex[mesh.Length];
        for (var i = 0; i < mesh.Length; i++)
        {
            var m = mesh[i];
            vertices[i] = new Vertex(m.ScreenX * 2 - 1, 1 - m.ScreenY * 2, m.CanvasU, m.CanvasV, m.LocalU, m.LocalV);
        }
        Draw(vertices);
        ctx.PSSetShaderResource(0, null!);
    }

    /// <summary>Quad covering (x, y, w, h) in normalized target coordinates, sampling the whole texture.</summary>
    private void DrawQuad(double x, double y, double w, double h)
    {
        float l = (float)(x * 2 - 1), r = (float)((x + w) * 2 - 1), t = (float)(1 - y * 2), b = (float)(1 - (y + h) * 2);
        Draw(new[]
        {
            new Vertex(l, t, 0, 0, 0, 0), new Vertex(r, t, 1, 0, 1, 0), new Vertex(l, b, 0, 1, 0, 1),
            new Vertex(r, t, 1, 0, 1, 0), new Vertex(r, b, 1, 1, 1, 1), new Vertex(l, b, 0, 1, 0, 1),
        });
    }

    private void Draw(Vertex[] vertices)
    {
        var ctx = _context!;
        if (vertices.Length > _vertexCapacity)
        {
            _vertices?.Dispose();
            _vertexCapacity = Math.Max(vertices.Length, 8192);
            _vertices = _device!.CreateBuffer(new BufferDescription(_vertexCapacity * VertexSize, BindFlags.VertexBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write, ResourceOptionFlags.None, 0));
        }
        var mapped = ctx.Map(_vertices!, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        unsafe
        {
            fixed (Vertex* src = vertices)
                Buffer.MemoryCopy(src, (void*)mapped.DataPointer, (long)_vertexCapacity * VertexSize, (long)vertices.Length * VertexSize);
        }
        ctx.Unmap(_vertices!, 0);
        ctx.IASetVertexBuffer(0, _vertices!, VertexSize, 0);
        ctx.Draw(vertices.Length, 0);
    }

    private void SetParams(Params p)
    {
        var ctx = _context!;
        var mapped = ctx.Map(_constants!, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        Marshal.StructureToPtr(p, mapped.DataPointer, false);
        ctx.Unmap(_constants!, 0);
    }

    private Canvas CreateCanvas(int w, int h)
    {
        var texture = _device!.CreateTexture2D(new Texture2DDescription
        {
            Width = w, Height = h, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });
        return new Canvas
        {
            Texture = texture, Width = w, Height = h,
            Target = _device.CreateRenderTargetView(texture),
            View = _device.CreateShaderResourceView(texture),
        };
    }

    /// <summary>GPU copy of a frame source, uploaded only when the source has a new frame.</summary>
    private CachedTexture? GetTexture(IFrameSource source, long frame)
    {
        var number = source.FrameNumber;
        if (number == 0 || source.Width <= 0 || source.Height <= 0) return null;

        if (!_textures.TryGetValue(source, out var cached) || cached.Width != source.Width || cached.Height != source.Height)
        {
            cached?.Dispose();
            var texture = _device!.CreateTexture2D(new Texture2DDescription
            {
                Width = source.Width, Height = source.Height, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Dynamic,
                BindFlags = BindFlags.ShaderResource, CPUAccessFlags = CpuAccessFlags.Write,
            });
            cached = new CachedTexture { Texture = texture, View = _device.CreateShaderResourceView(texture), Width = source.Width, Height = source.Height };
            _textures[source] = cached;
        }
        cached.LastUsed = frame;

        if (cached.Frame != number)
        {
            var mapped = _context!.Map(cached.Texture, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
            var ok = source.CopyTo(mapped.DataPointer, mapped.RowPitch);
            _context.Unmap(cached.Texture, 0);
            if (ok) cached.Frame = number;
        }
        return cached.Frame >= 0 ? cached : null;
    }

    /// <summary>Frees textures of sources not drawn for a couple of seconds (finished cues).</summary>
    private void PruneCaches(long frame)
    {
        if (frame % 60 != 0) return;
        foreach (var key in _textures.Where(kv => frame - kv.Value.LastUsed > 120).Select(kv => kv.Key).ToList())
        {
            _textures[key].Dispose();
            _textures.Remove(key);
        }
        foreach (var key in _canvases.Where(kv => frame - kv.Value.LastUsed > 120).Select(kv => kv.Key).ToList())
        {
            _canvases[key].Dispose();
            _canvases.Remove(key);
        }
    }

    public void Dispose()
    {
        _stop = true;
        if (Thread.CurrentThread != _thread) _thread.Join(2000);
    }
}
