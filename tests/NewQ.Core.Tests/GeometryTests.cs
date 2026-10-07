using NewQ.Core.Model;
using NewQ.Core.Video;
using Xunit;

namespace NewQ.Core.Tests;

public class GeometryTests
{
    private static void Near(double expected, double actual, double tol = 1e-6) => Assert.InRange(actual, expected - tol, expected + tol);

    [Fact]
    public void Default_output_covers_the_screen_and_the_whole_canvas()
    {
        var mesh = OutputGeometry.BuildMesh(new VideoOutput(), resolution: 4);
        Assert.Equal(4 * 4 * 6, mesh.Length);
        Assert.Equal(0, mesh.Min(v => v.ScreenX)); Assert.Equal(1, mesh.Max(v => v.ScreenX));
        Assert.Equal(0, mesh.Min(v => v.ScreenY)); Assert.Equal(1, mesh.Max(v => v.ScreenY));
        Assert.All(mesh, v => { Near(v.ScreenX, v.CanvasU); Near(v.ScreenY, v.CanvasV); });
    }

    [Fact]
    public void Crop_and_placement()
    {
        var o = new VideoOutput { SourceX = 0.5, SourceWidth = 0.5, DestX = 0.1, DestY = 0.2, DestWidth = 0.5, DestHeight = 0.5 };
        var mesh = OutputGeometry.BuildMesh(o, 2);
        Near(0.1, mesh.Min(v => v.ScreenX)); Near(0.6, mesh.Max(v => v.ScreenX));
        Near(0.2, mesh.Min(v => v.ScreenY)); Near(0.7, mesh.Max(v => v.ScreenY));
        Near(0.5, mesh.Min(v => v.CanvasU)); Near(1.0, mesh.Max(v => v.CanvasU));
        Near(0.0, mesh.Min(v => v.LocalU)); Near(1.0, mesh.Max(v => v.LocalU)); // blend coordinates are per output
    }

    [Fact]
    public void Keystone_moves_corners_and_keeps_lines_straight()
    {
        var o = new VideoOutput { TopLeft = new(0.1, 0.05), TopRight = new(-0.1, 0.0), BottomLeft = new(0.0, 0.0), BottomRight = new(0.0, -0.05) };
        var c = OutputGeometry.Corners(o);
        Near(0.1, c[0].X); Near(0.05, c[0].Y);
        Near(0.9, c[1].X);
        Near(0.95, c[2].Y);

        var h = Homography.FromUnitSquare(c[0], c[1], c[2], c[3]);
        var (x0, y0) = h.Map(0, 0); var (x1, y1) = h.Map(1, 0); var (x2, y2) = h.Map(1, 1); var (x3, y3) = h.Map(0, 1);
        Near(c[0].X, x0); Near(c[0].Y, y0); Near(c[1].X, x1); Near(c[2].Y, y2); Near(c[3].X, x3);

        // Points along a horizontal line of the image stay collinear on the screen (projective, not bilinear).
        var a = h.Map(0, 0.3); var m = h.Map(0.4, 0.3); var b = h.Map(1, 0.3);
        var cross = (m.X - a.X) * (b.Y - a.Y) - (m.Y - a.Y) * (b.X - a.X);
        Near(0, cross, 1e-9);
        Assert.True(y3 > y0);
    }

    [Fact]
    public void Warp_offsets_bend_the_image_smoothly()
    {
        var o = new VideoOutput { WarpColumns = 2, WarpRows = 2 };
        // No offsets: identical to no warp.
        Assert.All(OutputGeometry.BuildMesh(o, 4), v => { Near(v.ScreenX, v.CanvasU, 1e-6); Near(v.ScreenY, v.CanvasV, 1e-6); });

        o.SetWarpPoint(1, 1, new NormalizedPoint(0.1, 0)); // push the centre right
        var (cx, _) = OutputGeometry.WarpOffset(o, 0.5, 0.5);
        var (ex, _) = OutputGeometry.WarpOffset(o, 0.0, 0.0);
        var (qx, _) = OutputGeometry.WarpOffset(o, 0.25, 0.5);
        Near(0.1, cx);
        Near(0.0, ex);
        Assert.InRange(qx, 0.02, 0.1); // falls off smoothly between control points
    }

    [Theory]
    [InlineData(FitMode.Fit, 0, 0.125, 1, 0.75)]       // 4:3 → wide canvas: bars left/right... here source is wider
    [InlineData(FitMode.Fill, -0.1666667, 0, 1.3333333, 1)]
    [InlineData(FitMode.Stretch, 0, 0, 1, 1)]
    public void Fit_modes(FitMode mode, double x, double y, double w, double h)
    {
        // Source 1920×1080 on a 1440×1080 (4:3) canvas.
        var r = OutputGeometry.Fit(mode, 1920, 1080, 1440, 1080);
        Near(x, r.X, 1e-6); Near(y, r.Y, 1e-6); Near(w, r.W, 1e-6); Near(h, r.H, 1e-6);
    }

    [Fact]
    public void Degenerate_quad_does_not_produce_nan()
    {
        var o = new VideoOutput { TopRight = new(-1, 0), BottomRight = new(-1, 0) }; // right edge collapsed onto the left
        Assert.All(OutputGeometry.BuildMesh(o, 4), v => Assert.False(float.IsNaN(v.ScreenX) || float.IsNaN(v.ScreenY)));
    }
}
