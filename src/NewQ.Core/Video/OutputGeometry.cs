using NewQ.Core.Model;

namespace NewQ.Core.Video;

/// <summary>One vertex of an output mesh.</summary>
/// <param name="ScreenX">Position on the output, normalized 0–1 (left→right).</param>
/// <param name="ScreenY">Position on the output, normalized 0–1 (top→bottom).</param>
/// <param name="CanvasU">Where to sample the route canvas, 0–1.</param>
/// <param name="CanvasV">Where to sample the route canvas, 0–1.</param>
/// <param name="LocalU">Position inside this output's crop, 0–1 (edge blending is computed from this).</param>
/// <param name="LocalV">Position inside this output's crop, 0–1.</param>
public readonly record struct MeshVertex(float ScreenX, float ScreenY, float CanvasU, float CanvasV, float LocalU, float LocalV);

/// <summary>
/// Turns a <see cref="VideoOutput"/> into a triangle mesh: crop of the canvas → placement on the screen →
/// keystone (projective corner pin, straight lines stay straight) → warp (smooth Catmull-Rom offset grid).
/// </summary>
public static class OutputGeometry
{
    /// <summary>Default grid density: dense enough for smooth warps and accurate perspective.</summary>
    public const int DefaultResolution = 32;

    /// <summary>Triangle list (3 vertices per triangle) for the output.</summary>
    public static MeshVertex[] BuildMesh(VideoOutput output, int resolution = DefaultResolution)
    {
        resolution = Math.Clamp(resolution, 1, 128);
        var corners = Corners(output);
        var h = Homography.FromUnitSquare(corners[0], corners[1], corners[2], corners[3]);

        var grid = new MeshVertex[(resolution + 1) * (resolution + 1)];
        for (var j = 0; j <= resolution; j++)
        for (var i = 0; i <= resolution; i++)
        {
            double u = (double)i / resolution, v = (double)j / resolution;
            var (x, y) = h.Map(u, v);
            if (output.HasWarp)
            {
                var (dx, dy) = WarpOffset(output, u, v);
                x += dx;
                y += dy;
            }
            grid[j * (resolution + 1) + i] = new MeshVertex(
                (float)x, (float)y,
                (float)(output.SourceX + u * output.SourceWidth), (float)(output.SourceY + v * output.SourceHeight),
                (float)u, (float)v);
        }

        var triangles = new MeshVertex[resolution * resolution * 6];
        var t = 0;
        for (var j = 0; j < resolution; j++)
        for (var i = 0; i < resolution; i++)
        {
            var a = grid[j * (resolution + 1) + i];
            var b = grid[j * (resolution + 1) + i + 1];
            var c = grid[(j + 1) * (resolution + 1) + i];
            var d = grid[(j + 1) * (resolution + 1) + i + 1];
            triangles[t++] = a; triangles[t++] = b; triangles[t++] = c;
            triangles[t++] = b; triangles[t++] = d; triangles[t++] = c;
        }
        return triangles;
    }

    /// <summary>The 4 corners on the screen (TL, TR, BR, BL) after placement and keystone.</summary>
    public static (double X, double Y)[] Corners(VideoOutput o)
    {
        double l = o.DestX, t = o.DestY, r = o.DestX + o.DestWidth, b = o.DestY + o.DestHeight;
        return new[]
        {
            (l + o.TopLeft.X, t + o.TopLeft.Y),
            (r + o.TopRight.X, t + o.TopRight.Y),
            (r + o.BottomRight.X, b + o.BottomRight.Y),
            (l + o.BottomLeft.X, b + o.BottomLeft.Y),
        };
    }

    /// <summary>Smooth (Catmull-Rom) interpolation of the warp control offsets at (u, v).</summary>
    public static (double X, double Y) WarpOffset(VideoOutput o, double u, double v)
    {
        var cols = o.WarpColumns;
        var rows = o.WarpRows;
        var gx = u * cols;
        var gy = v * rows;
        var ix = Math.Clamp((int)Math.Floor(gx), 0, cols - 1);
        var iy = Math.Clamp((int)Math.Floor(gy), 0, rows - 1);
        var fx = gx - ix;
        var fy = gy - iy;

        double sx = 0, sy = 0;
        for (var m = -1; m <= 2; m++)
        {
            var wy = CatmullRomWeight(m, fy);
            var row = Math.Clamp(iy + m, 0, rows);
            for (var n = -1; n <= 2; n++)
            {
                var w = wy * CatmullRomWeight(n, fx);
                var p = o.GetWarpPoint(Math.Clamp(ix + n, 0, cols), row);
                sx += w * p.X;
                sy += w * p.Y;
            }
        }
        return (sx, sy);
    }

    private static double CatmullRomWeight(int k, double t) => k switch
    {
        -1 => ((-t + 2) * t - 1) * t / 2,
        0 => ((3 * t - 5) * t * t + 2) / 2,
        1 => ((-3 * t + 4) * t + 1) * t / 2,
        _ => (t - 1) * t * t / 2,
    };

    /// <summary>
    /// Rectangle (normalized to the canvas) where a source of the given size is drawn for a fit mode.
    /// </summary>
    public static (double X, double Y, double W, double H) Fit(FitMode mode, double sourceWidth, double sourceHeight, double canvasWidth, double canvasHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0) return (0, 0, 1, 1);
        double scaleX = canvasWidth / sourceWidth, scaleY = canvasHeight / sourceHeight;
        double scale = mode switch
        {
            FitMode.Fill => Math.Max(scaleX, scaleY),
            FitMode.Original => 1,
            _ => Math.Min(scaleX, scaleY),
        };
        if (mode == FitMode.Stretch) return (0, 0, 1, 1);
        double w = sourceWidth * scale / canvasWidth, h = sourceHeight * scale / canvasHeight;
        return ((1 - w) / 2, (1 - h) / 2, w, h);
    }
}

/// <summary>Projective transform from the unit square to an arbitrary quadrilateral.</summary>
public readonly struct Homography
{
    private readonly double _a, _b, _c, _d, _e, _f, _g, _h;

    private Homography(double a, double b, double c, double d, double e, double f, double g, double h)
        => (_a, _b, _c, _d, _e, _f, _g, _h) = (a, b, c, d, e, f, g, h);

    /// <summary>Maps (0,0)→p0, (1,0)→p1, (1,1)→p2, (0,1)→p3 (Heckbert's square-to-quad).</summary>
    public static Homography FromUnitSquare((double X, double Y) p0, (double X, double Y) p1, (double X, double Y) p2, (double X, double Y) p3)
    {
        double sx = p0.X - p1.X + p2.X - p3.X;
        double sy = p0.Y - p1.Y + p2.Y - p3.Y;
        if (Math.Abs(sx) < 1e-12 && Math.Abs(sy) < 1e-12)
        {
            // Parallelogram: affine.
            return new Homography(p1.X - p0.X, p2.X - p1.X, p0.X, p1.Y - p0.Y, p2.Y - p1.Y, p0.Y, 0, 0);
        }
        double dx1 = p1.X - p2.X, dx2 = p3.X - p2.X, dy1 = p1.Y - p2.Y, dy2 = p3.Y - p2.Y;
        double det = dx1 * dy2 - dx2 * dy1;
        if (Math.Abs(det) < 1e-12) det = 1e-12; // degenerate quad: avoid NaN, the result is just squashed
        double g = (sx * dy2 - dx2 * sy) / det;
        double h = (dx1 * sy - sx * dy1) / det;
        return new Homography(
            p1.X - p0.X + g * p1.X, p3.X - p0.X + h * p3.X, p0.X,
            p1.Y - p0.Y + g * p1.Y, p3.Y - p0.Y + h * p3.Y, p0.Y,
            g, h);
    }

    public (double X, double Y) Map(double u, double v)
    {
        var w = _g * u + _h * v + 1;
        return ((_a * u + _b * v + _c) / w, (_d * u + _e * v + _f) / w);
    }
}
