using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using NewQ.Core.Model;
using NewQ.Core.Video;

namespace NewQ.App.Video.Gpu;

public enum TestPatternKind
{
    ColorBars = 0, Grid = 1, Checkerboard = 2, GrayRamp = 3,
    White = 4, Black = 5, Red = 6, Green = 7, Blue = 8, Gray50 = 9,
    Motion = 10,
}

/// <summary>Something that supplies BGRA frames (a video, an image).</summary>
public interface IFrameSource
{
    int Width { get; }
    int Height { get; }

    /// <summary>Changes every time a new frame is available (0 = nothing yet).</summary>
    long FrameNumber { get; }

    /// <summary>Copies the current frame (BGRA, <see cref="Width"/>×<see cref="Height"/>) into a mapped texture.</summary>
    bool CopyTo(IntPtr destination, int destinationPitch);
}

/// <summary>Linear opacity ramp, evaluated by the renderers on the shared clock.</summary>
public sealed record OpacityRamp(double From, double To, double StartSeconds, double DurationSeconds)
{
    public double ValueAt(double now)
    {
        if (DurationSeconds <= 0 || now >= StartSeconds + DurationSeconds) return To;
        if (now <= StartSeconds) return From;
        return From + (To - From) * (now - StartSeconds) / DurationSeconds;
    }
}

/// <summary>A picture on a route canvas. Opacity changes are atomic swaps, safe from any thread.</summary>
public sealed class CompositionLayer
{
    private OpacityRamp _opacity;

    public CompositionLayer(IFrameSource? source, TestPatternKind? pattern, int zLayer, long order, FitMode fit, double clockNow)
    {
        Source = source;
        Pattern = pattern;
        ZLayer = zLayer;
        Order = order;
        Fit = fit;
        _opacity = new OpacityRamp(0, 0, clockNow, 0);
    }

    public IFrameSource? Source { get; }
    public TestPatternKind? Pattern { get; }
    public int ZLayer { get; }
    public long Order { get; }
    public FitMode Fit { get; }

    public OpacityRamp Opacity => System.Threading.Volatile.Read(ref _opacity);

    /// <summary>Animates from the current value to <paramref name="to"/>.</summary>
    public void AnimateOpacity(double to, double seconds, double now)
        => System.Threading.Volatile.Write(ref _opacity, new OpacityRamp(Opacity.ValueAt(now), Math.Clamp(to, 0, 1), now, Math.Max(0, seconds)));
}

/// <summary>Immutable geometry of one output, ready for the renderer.</summary>
public sealed record OutputSnapshot(
    Guid RouteId, int Index, string Name, int ScreenIndex, MeshVertex[] Mesh,
    double BlendLeft, double BlendRight, double BlendTop, double BlendBottom, double BlendGamma, double BlendCurve);

/// <summary>Immutable view of a route for one frame.</summary>
public sealed record RouteSnapshot(Guid RouteId, int CanvasWidth, int CanvasHeight, ImmutableArray<CompositionLayer> Layers, ImmutableArray<OutputSnapshot> Outputs);

/// <summary>
/// Shared state between the UI thread (cues add/remove layers, routes change) and the render threads (one per
/// screen, reading immutable snapshots each vsync). All writes take a lock and publish new immutable arrays.
/// </summary>
public sealed class CompositionHub
{
    private readonly object _lock = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private ImmutableDictionary<Guid, RouteSnapshot> _routes = ImmutableDictionary<Guid, RouteSnapshot>.Empty;
    private readonly Dictionary<Guid, CompositionLayer> _testPatterns = new();
    private long _order;
    private volatile bool _identify;

    /// <summary>Seconds on the clock shared by all renderers (fades, test-pattern motion).</summary>
    public double Now => _clock.Elapsed.TotalSeconds;

    /// <summary>Shows each output's number and a coloured frame (to tell projectors apart while patching).</summary>
    public bool IdentifyOutputs { get => _identify; set => _identify = value; }

    /// <summary>Raised (on the caller's thread) when the set of screens used by routes changes.</summary>
    public event Action? ScreensChanged;

    /// <summary>Replaces route geometry (keeps the layers of routes that still exist).</summary>
    public void SetRoutes(IReadOnlyList<VideoRoute> routes)
    {
        bool screensChanged;
        lock (_lock)
        {
            var oldScreens = UsedScreens();
            var builder = ImmutableDictionary.CreateBuilder<Guid, RouteSnapshot>();
            foreach (var route in routes)
            {
                var layers = _routes.TryGetValue(route.Id, out var old) ? old.Layers : ImmutableArray<CompositionLayer>.Empty;
                var outputs = route.Outputs.Select((o, i) => new OutputSnapshot(
                    route.Id, i, o.Name, o.ScreenIndex, OutputGeometry.BuildMesh(o),
                    o.BlendLeft, o.BlendRight, o.BlendTop, o.BlendBottom, o.BlendGamma, o.BlendCurve)).ToImmutableArray();
                builder[route.Id] = new RouteSnapshot(route.Id, route.CanvasWidth, route.CanvasHeight, layers, outputs);
            }
            _routes = builder.ToImmutable();
            screensChanged = !oldScreens.SetEquals(UsedScreens());
        }
        if (screensChanged) ScreensChanged?.Invoke();
    }

    /// <summary>Screens (monitor indexes, −1 = preview) that at least one route output uses.</summary>
    public HashSet<int> UsedScreens()
    {
        var routes = System.Threading.Volatile.Read(ref _routes);
        return routes.Values.SelectMany(r => r.Outputs).Select(o => o.ScreenIndex).ToHashSet();
    }

    public bool HasRoute(Guid routeId) => System.Threading.Volatile.Read(ref _routes).ContainsKey(routeId);

    /// <summary>Everything a renderer for this screen must draw this frame.</summary>
    public IReadOnlyList<(RouteSnapshot Route, OutputSnapshot Output)> SnapshotFor(int screenIndex)
    {
        var routes = System.Threading.Volatile.Read(ref _routes);
        var result = new List<(RouteSnapshot, OutputSnapshot)>();
        foreach (var route in routes.Values)
            foreach (var output in route.Outputs)
                if (output.ScreenIndex == screenIndex) result.Add((route, output));
        return result;
    }

    public CompositionLayer AddLayer(Guid routeId, IFrameSource? source, TestPatternKind? pattern, int zLayer, FitMode fit)
    {
        lock (_lock)
        {
            var layer = new CompositionLayer(source, pattern, zLayer, ++_order, fit, Now);
            UpdateLayers(routeId, layers => layers.Add(layer));
            return layer;
        }
    }

    public void RemoveLayer(Guid routeId, CompositionLayer layer)
    {
        lock (_lock) UpdateLayers(routeId, layers => layers.Remove(layer));
    }

    /// <summary>Shows a test pattern on top of everything on the route (null = remove it).</summary>
    public void SetTestPattern(Guid routeId, TestPatternKind? kind)
    {
        lock (_lock)
        {
            if (_testPatterns.Remove(routeId, out var old)) UpdateLayers(routeId, layers => layers.Remove(old));
            if (kind is null) return;
            var layer = new CompositionLayer(null, kind, int.MaxValue, long.MaxValue, FitMode.Stretch, Now);
            layer.AnimateOpacity(1, 0, Now);
            _testPatterns[routeId] = layer;
            UpdateLayers(routeId, layers => layers.Add(layer));
        }
    }

    public TestPatternKind? TestPatternOn(Guid routeId)
    {
        lock (_lock) return _testPatterns.TryGetValue(routeId, out var layer) ? layer.Pattern : null;
    }

    /// <summary>Removes every test pattern (Panic, Safe mode, closing the tool).</summary>
    public void ClearTestPatterns()
    {
        lock (_lock)
            foreach (var routeId in _testPatterns.Keys.ToList())
                SetTestPattern(routeId, null);
    }

    private void UpdateLayers(Guid routeId, Func<ImmutableArray<CompositionLayer>, ImmutableArray<CompositionLayer>> change)
    {
        if (!_routes.TryGetValue(routeId, out var route)) return;
        var layers = change(route.Layers).Sort((a, b) => a.ZLayer != b.ZLayer ? a.ZLayer.CompareTo(b.ZLayer) : a.Order.CompareTo(b.Order));
        _routes = _routes.SetItem(routeId, route with { Layers = layers });
    }
}
