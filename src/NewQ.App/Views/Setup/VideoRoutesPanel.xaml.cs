using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using NewQ.App.Video.Gpu;
using NewQ.Core.Model;
using Forms = System.Windows.Forms;

namespace NewQ.App.Views.Setup;

public partial class VideoRoutesPanel : UserControl
{
    private SetupContext? _context;

    public VideoRoutesPanel()
    {
        InitializeComponent();
    }

    public void Initialize(SetupContext context)
    {
        _context = context;
        ScreenBox.ItemsSource = context.Screens;
        RouteList.ItemsSource = context.Workspace.VideoRoutes;
        RouteList.SelectedIndex = 0;
    }

    private VideoRoute? SelectedRoute => RouteList.SelectedItem as VideoRoute;
    private VideoOutput? SelectedOutput => OutputList.SelectedItem as VideoOutput;

    private void OnRouteSelected(object sender, SelectionChangedEventArgs e)
    {
        var route = SelectedRoute;
        RouteEditor.DataContext = route;
        RouteEditor.IsEnabled = route is not null;
        OutputList.ItemsSource = route?.Outputs;
        OutputList.SelectedIndex = route?.Outputs.Count > 0 ? 0 : -1;
        GridToggle.IsChecked = route is not null && _context?.Hub.TestPatternOn(route.Id) == TestPatternKind.Grid;
        IdentifyToggle.IsChecked = _context?.Hub.IdentifyOutputs == true;
        OnOutputSelected(sender, e);
    }

    private void OnOutputSelected(object sender, SelectionChangedEventArgs e)
    {
        var output = SelectedOutput;
        OutputEditor.DataContext = output;
        OutputEditor.IsEnabled = output is not null;
        Geometry.Output = output;
        UpdateAspect();
    }

    private void OnScreenChanged(object sender, SelectionChangedEventArgs e) => UpdateAspect();

    private void UpdateAspect()
    {
        var size = ScreenSize(SelectedOutput?.ScreenIndex ?? VideoOutput.PreviewWindow);
        Geometry.ScreenAspect = (double)size.Width / size.Height;
    }

    /// <summary>Pixel size of a monitor (the preview counts as 1920×1080).</summary>
    private static System.Drawing.Size ScreenSize(int screenIndex)
    {
        var screens = Forms.Screen.AllScreens;
        return screenIndex >= 0 && screenIndex < screens.Length ? screens[screenIndex].Bounds.Size : new System.Drawing.Size(1920, 1080);
    }

    // ------------------------------------------------------------------ routes

    private void OnAddRoute(object sender, RoutedEventArgs e)
    {
        if (_context is null) return;
        var route = Workspace.CreateVideoRoute(VideoOutput.PreviewWindow);
        route.Name = SetupHelpers.UniqueName("Video", _context.Workspace.VideoRoutes.Select(r => r.Name));
        _context.Workspace.VideoRoutes.Add(route);
        RouteList.SelectedItem = route;
    }

    private void OnDuplicateRoute(object sender, RoutedEventArgs e)
    {
        if (_context is null || SelectedRoute is not VideoRoute source) return;
        var copy = new VideoRoute
        {
            Name = SetupHelpers.UniqueName(source.Name, _context.Workspace.VideoRoutes.Select(r => r.Name)),
            CanvasWidth = source.CanvasWidth,
            CanvasHeight = source.CanvasHeight,
        };
        foreach (var output in source.Outputs) copy.Outputs.Add(Clone(output, output.Name));
        _context.Workspace.VideoRoutes.Add(copy);
        RouteList.SelectedItem = copy;
    }

    private void OnRemoveRoute(object sender, RoutedEventArgs e)
    {
        if (_context is null || SelectedRoute is not VideoRoute route) return;
        var ws = _context.Workspace;
        if (ws.VideoRoutes.Count == 1)
        {
            MessageBox.Show("Il workspace deve avere almeno una route video.", "Route video", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var users = ws.Cues.OfType<VisualCue>().Where(c => c.VideoRouteId == route.Id).ToList();
        var replacement = ws.VideoRoutes.First(r => r != route);
        if (users.Count > 0 && MessageBox.Show(
                $"{users.Count} cue usano \"{route.Name}\": passeranno a \"{replacement.Name}\". Continuare?",
                "Route video", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        foreach (var cue in users) cue.VideoRouteId = replacement.Id;
        _context.Hub.SetTestPattern(route.Id, null);
        ws.VideoRoutes.Remove(route);
        RouteList.SelectedItem = replacement;
    }

    private void OnGridToggle(object sender, RoutedEventArgs e)
    {
        if (_context is null || SelectedRoute is not VideoRoute route) return;
        _context.Hub.SetTestPattern(route.Id, GridToggle.IsChecked == true ? TestPatternKind.Grid : null);
    }

    private void OnIdentifyToggle(object sender, RoutedEventArgs e)
    {
        if (_context is null) return;
        _context.Hub.IdentifyOutputs = IdentifyToggle.IsChecked == true;
    }

    /// <summary>
    /// Lays the route's outputs side by side across the canvas with the given overlap, and sets matching edge
    /// blends: the classic multi-projector blend in one click.
    /// </summary>
    private void OnTileOutputs(object sender, RoutedEventArgs e)
    {
        if (SelectedRoute is not VideoRoute route) return;
        var n = route.Outputs.Count;
        if (n < 2)
        {
            MessageBox.Show("Servono almeno due uscite nella route.", "Affianca uscite", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!double.TryParse(OverlapBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var percent) || percent < 0 || percent > 50)
        {
            MessageBox.Show("Sovrapposizione: un valore tra 0 e 50 %.", "Affianca uscite", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var p = percent / 100;
        var width = 1 / (n - (n - 1) * p); // one output, as a fraction of the canvas
        for (var i = 0; i < n; i++)
        {
            var o = route.Outputs[i];
            o.SourceX = i * width * (1 - p);
            o.SourceY = 0;
            o.SourceWidth = width;
            o.SourceHeight = 1;
            o.BlendLeft = i > 0 ? p : 0;
            o.BlendRight = i < n - 1 ? p : 0;
        }
        var screen = ScreenSize(route.Outputs[0].ScreenIndex);
        route.CanvasWidth = (int)Math.Round(screen.Width * (n - (n - 1) * p));
        route.CanvasHeight = screen.Height;
    }

    // ------------------------------------------------------------------ outputs

    private void OnAddOutput(object sender, RoutedEventArgs e)
    {
        if (SelectedRoute is not VideoRoute route) return;
        var output = new VideoOutput { Name = SetupHelpers.UniqueName("Uscita", route.Outputs.Select(o => o.Name)) };
        route.Outputs.Add(output);
        OutputList.SelectedItem = output;
    }

    private void OnDuplicateOutput(object sender, RoutedEventArgs e)
    {
        if (SelectedRoute is not VideoRoute route || SelectedOutput is not VideoOutput source) return;
        var copy = Clone(source, SetupHelpers.UniqueName(source.Name, route.Outputs.Select(o => o.Name)));
        route.Outputs.Add(copy);
        OutputList.SelectedItem = copy;
    }

    private void OnRemoveOutput(object sender, RoutedEventArgs e)
    {
        if (SelectedRoute is not VideoRoute route || SelectedOutput is not VideoOutput output) return;
        if (route.Outputs.Count == 1 && MessageBox.Show(
                $"\"{route.Name}\" resterà senza uscite: le sue cue non saranno visibili. Continuare?",
                "Route video", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        var index = route.Outputs.IndexOf(output);
        route.Outputs.Remove(output);
        OutputList.SelectedIndex = Math.Min(index, route.Outputs.Count - 1);
    }

    private void OnResetKeystone(object sender, RoutedEventArgs e)
    {
        if (SelectedOutput is not VideoOutput o) return;
        o.TopLeft = o.TopRight = o.BottomRight = o.BottomLeft = NormalizedPoint.Zero;
    }

    private void OnResetWarp(object sender, RoutedEventArgs e)
    {
        if (SelectedOutput is not VideoOutput o || !o.HasWarp) return;
        o.WarpPoints = o.WarpPoints.Select(_ => NormalizedPoint.Zero).ToList();
    }

    private static VideoOutput Clone(VideoOutput s, string name)
    {
        var copy = new VideoOutput
        {
            Name = name, ScreenIndex = s.ScreenIndex,
            SourceX = s.SourceX, SourceY = s.SourceY, SourceWidth = s.SourceWidth, SourceHeight = s.SourceHeight,
            DestX = s.DestX, DestY = s.DestY, DestWidth = s.DestWidth, DestHeight = s.DestHeight,
            TopLeft = s.TopLeft, TopRight = s.TopRight, BottomRight = s.BottomRight, BottomLeft = s.BottomLeft,
            WarpColumns = s.WarpColumns, WarpRows = s.WarpRows,
            BlendLeft = s.BlendLeft, BlendRight = s.BlendRight, BlendTop = s.BlendTop, BlendBottom = s.BlendBottom,
            BlendGamma = s.BlendGamma, BlendCurve = s.BlendCurve,
        };
        copy.WarpPoints = s.WarpPoints.ToList();
        return copy;
    }
}
