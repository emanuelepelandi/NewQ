using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using NewQ.App.Video.Gpu;
using NewQ.Core;
using NewQ.Core.Model;

namespace NewQ.App.Views.Setup;

public partial class TestVideoPanel : UserControl
{
    public sealed record PatternInfo(TestPatternKind Kind, string Title, string Purpose);

    /// <summary>A route with its checkbox and the pattern currently on it.</summary>
    public sealed class RouteChoice : ObservableObject
    {
        private bool _selected = true;
        private string _status = "";

        public RouteChoice(VideoRoute route) => Route = route;

        public VideoRoute Route { get; }
        public bool Selected { get => _selected; set => SetField(ref _selected, value); }
        public string Status { get => _status; set => SetField(ref _status, value); }
    }

    public static readonly IReadOnlyList<PatternInfo> All = new[]
    {
        new PatternInfo(TestPatternKind.Motion, "Fluidità (movimento)", "Scatti, judder, frame persi."),
        new PatternInfo(TestPatternKind.Grid, "Griglia di allineamento", "Keystone, warp, fuoco, overscan."),
        new PatternInfo(TestPatternKind.ColorBars, "Barre colore", "Colori, canali RGB, saturazione."),
        new PatternInfo(TestPatternKind.GrayRamp, "Rampa di grigi", "Gamma, nero, banding."),
        new PatternInfo(TestPatternKind.Checkerboard, "Scacchiera", "Nitidezza, contrasto, convergenza."),
        new PatternInfo(TestPatternKind.White, "Bianco pieno", "Uniformità, hot spot, blend."),
        new PatternInfo(TestPatternKind.Gray50, "Grigio 50 %", "Uniformità, bande nel blend."),
        new PatternInfo(TestPatternKind.Black, "Nero", "Nero, luce residua nel blend."),
        new PatternInfo(TestPatternKind.Red, "Rosso", "Pixel difettosi, uniformità."),
        new PatternInfo(TestPatternKind.Green, "Verde", "Pixel difettosi, uniformità."),
        new PatternInfo(TestPatternKind.Blue, "Blu", "Pixel difettosi, uniformità."),
    };

    private SetupContext? _context;
    private List<RouteChoice> _routes = new();

    public TestVideoPanel()
    {
        InitializeComponent();
        Patterns.ItemsSource = All;
        IsVisibleChanged += (_, _) => { if (IsVisible) Refresh(); };
    }

    public void Initialize(SetupContext context)
    {
        _context = context;
        Refresh();
    }

    /// <summary>Re-reads the routes (they can change in the "Route video" tab) and what is on them.</summary>
    private void Refresh()
    {
        if (_context is null) return;
        var previous = _routes.ToDictionary(r => r.Route.Id, r => r.Selected);
        _routes = _context.Workspace.VideoRoutes
            .Select(r => new RouteChoice(r) { Selected = previous.TryGetValue(r.Id, out var s) ? s : true })
            .ToList();
        RouteChecks.ItemsSource = _routes;
        IdentifyToggle.IsChecked = _context.Hub.IdentifyOutputs;
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (_context is null) return;
        foreach (var choice in _routes)
        {
            var kind = _context.Hub.TestPatternOn(choice.Route.Id);
            choice.Status = kind is TestPatternKind k ? All.First(p => p.Kind == k).Title : "";
        }
    }

    private void OnPattern(object sender, RoutedEventArgs e)
    {
        if (_context is null || ((FrameworkElement)sender).Tag is not TestPatternKind kind) return;
        var targets = _routes.Where(r => r.Selected).ToList();
        if (targets.Count == 0)
        {
            MessageBox.Show("Seleziona almeno una route.", "Test video", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        foreach (var choice in targets) _context.Hub.SetTestPattern(choice.Route.Id, kind);
        _context.Log(Core.Engine.EngineLogLevel.Info, $"Test pattern \"{All.First(p => p.Kind == kind).Title}\" su {string.Join(", ", targets.Select(t => t.Route.Name))}");
        UpdateStatus();
    }

    private void OnClearAll(object sender, RoutedEventArgs e)
    {
        if (_context is null) return;
        _context.Hub.ClearTestPatterns();
        _context.Hub.IdentifyOutputs = false;
        IdentifyToggle.IsChecked = false;
        UpdateStatus();
    }

    private void OnIdentify(object sender, RoutedEventArgs e)
    {
        if (_context is null) return;
        _context.Hub.IdentifyOutputs = IdentifyToggle.IsChecked == true;
    }

    private void OnSelectAll(object sender, RoutedEventArgs e) { foreach (var r in _routes) r.Selected = true; }
    private void OnSelectNone(object sender, RoutedEventArgs e) { foreach (var r in _routes) r.Selected = false; }
}
