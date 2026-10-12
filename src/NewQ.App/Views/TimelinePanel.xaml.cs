using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using NewQ.App.ViewModels;
using NewQ.Core.Timeline;

namespace NewQ.App.Views;

/// <summary>The timeline under the cue list: header (lanes, zoom, follow), the drawing and its scroll bars.</summary>
public partial class TimelinePanel : UserControl
{
    private MainViewModel? _vm;
    private readonly DispatcherTimer _nowTimer;

    public TimelinePanel()
    {
        InitializeComponent();
        _nowTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(50) };
        _nowTimer.Tick += (_, _) => UpdateNow();
        View.ScrollChanged += UpdateScrollBars;
        View.CueClicked += cue => { if (_vm is not null) _vm.SelectedCue = cue; };
        View.Zoomed += SaveZoom;
        IsVisibleChanged += (_, _) => { if (IsVisible) _nowTimer.Start(); else _nowTimer.Stop(); };
    }

    public void Initialize(MainViewModel vm)
    {
        _syncing = true; // setting the toggles below must not save anything
        _vm = vm;
        var s = vm.Settings;
        View.LanesByRoute = s.TimelineLanesByRoute;
        View.PixelsPerSecond = s.TimelineZoom;
        View.ThumbnailOf = vm.ThumbnailOf;
        ByRoute.IsChecked = s.TimelineLanesByRoute;
        ByType.IsChecked = !s.TimelineLanesByRoute;
        Follow.IsChecked = s.TimelineFollow;
        _syncing = false;
        vm.PropertyChanged += OnViewModelChanged;
        vm.Thumbnails.Updated += View.Redraw;
        vm.TimelineStateChanged += View.Redraw; // fading cues turn amber
        Infrastructure.CueColors.Changed += UpdateNow;
        Refresh();
        if (IsVisible) _nowTimer.Start();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.Timeline):
            case nameof(MainViewModel.RoutesVersion):
                Refresh();
                break;
            case nameof(MainViewModel.SelectedCue):
                View.PlayheadCue = _vm!.SelectedCue;
                View.SelectedCue = _vm.SelectedCue;
                View.Redraw();
                // While the show plays the cursor leads (FollowCursor); stopped, the view shows the next GO.
                if (Follow.IsChecked == true && !_showRunning && _vm.SelectedCue is { } cue) View.ScrollToBlock(_vm.Timeline.BlockOf(cue));
                break;
        }
    }

    private void Refresh()
    {
        if (_vm is null) return;
        View.PlayheadCue = _vm.SelectedCue;
        View.SelectedCue = _vm.SelectedCue;
        View.Update(_vm.Timeline, _vm.Workspace);
    }

    private readonly List<(Rect Bar, double X)> _progress = new();

    /// <summary>
    /// The running cues' bars fill up as they play (only that thin layer is redrawn); the show's cursors, not drawn,
    /// lead "Segui il playhead".
    /// </summary>
    private void UpdateNow()
    {
        double? lead = null; // the cursor of the latest GO (furthest on), or the wait for the next GO
        TimelinePoint? leadAt = null;
        var running = false;
        _progress.Clear();
        if (_vm is not null)
        {
            foreach (var now in _vm.TimelineCursors())
            {
                var x = now.WaitingForGo ? View.ScreenXOfGo(now.At.Block + 1) : View.ScreenXOf(now.At);
                running |= !now.WaitingForGo;
                var at = now.WaitingForGo ? new TimelinePoint(now.At.Block + 1, -1) : now.At;
                if (leadAt is not TimelinePoint best || at.Block > best.Block || at.Block == best.Block && at.Time > best.Time)
                {
                    leadAt = at;
                    lead = x;
                }
            }
            foreach (var p in _vm.CueProgress())
                if (View.TryGetScreenBounds(p.Item, out var bar)) _progress.Add((bar, View.ScreenXOf(p.At)));
        }
        ProgressLayer.Set(_progress);
        _showRunning = running;
        if (lead is double leadX) FollowCursor(leadX);
    }

    private bool _following;
    private bool _showRunning; // a green cursor is on the timeline

    /// <summary>"Segui il playhead": when the show's cursor leaves the view, the timeline moves on with it.</summary>
    private void FollowCursor(double x)
    {
        if (_following || Follow.IsChecked != true) return;
        var left = Controls.TimelineView.GutterWidth;
        var right = left + View.ViewportWidth;
        if (x >= left && x <= right - 8) return;
        _following = true;
        try { View.HorizontalOffset += x - (left + View.ViewportWidth * 0.15); }
        finally { _following = false; }
    }

    private void UpdateScrollBars()
    {
        HScroll.Maximum = Math.Max(0, View.ContentWidth - View.ViewportWidth);
        HScroll.ViewportSize = View.ViewportWidth;
        HScroll.LargeChange = Math.Max(1, View.ViewportWidth * 0.8);
        HScroll.SmallChange = 40;
        HScroll.Value = View.HorizontalOffset;
        HScroll.Visibility = HScroll.Maximum > 0 ? Visibility.Visible : Visibility.Collapsed;

        VScroll.Maximum = Math.Max(0, View.ContentHeight - View.ViewportHeight);
        VScroll.ViewportSize = View.ViewportHeight;
        VScroll.LargeChange = Math.Max(1, View.ViewportHeight * 0.8);
        VScroll.SmallChange = 30;
        VScroll.Value = View.VerticalOffset;
        VScroll.Visibility = VScroll.Maximum > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateNow();
    }

    private void OnHScroll(object sender, ScrollEventArgs e) => View.HorizontalOffset = e.NewValue;
    private void OnVScroll(object sender, ScrollEventArgs e) => View.VerticalOffset = e.NewValue;

    private bool _syncing;

    /// <summary>Route / Tipo behave as a pair of radio buttons (also from the keyboard and UI automation).</summary>
    private void SetLanes(bool byRoute)
    {
        if (_syncing) return;
        _syncing = true;
        ByRoute.IsChecked = byRoute;
        ByType.IsChecked = !byRoute;
        _syncing = false;
        if (View.LanesByRoute == byRoute && _vm is not null && _vm.Settings.TimelineLanesByRoute == byRoute) return;
        View.LanesByRoute = byRoute;
        Refresh();
        _vm?.SaveTimelineSettings(s => s.TimelineLanesByRoute = byRoute);
    }

    private void OnLanesByRoute(object sender, RoutedEventArgs e) => SetLanes(true);
    private void OnLanesByType(object sender, RoutedEventArgs e) => SetLanes(false);

    /// <summary>The active choice can't be switched off directly: it stays on.</summary>
    private void OnLaneUnchecked(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        SetLanes(View.LanesByRoute);
    }

    private void OnZoomIn(object sender, RoutedEventArgs e) { View.ZoomAt(1.5, TimelineViewCenter()); SaveZoom(); }
    private void OnZoomOut(object sender, RoutedEventArgs e) { View.ZoomAt(1 / 1.5, TimelineViewCenter()); SaveZoom(); }
    private void OnFit(object sender, RoutedEventArgs e) { View.ZoomToFit(); SaveZoom(); }

    private double TimelineViewCenter() => Controls.TimelineView.GutterWidth + View.ViewportWidth / 2;

    private void SaveZoom() => _vm?.SaveTimelineSettings(s => s.TimelineZoom = View.PixelsPerSecond);

    private void OnFollow(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        var follow = Follow.IsChecked == true;
        _vm?.SaveTimelineSettings(s => s.TimelineFollow = follow);
        if (follow && _vm?.SelectedCue is { } cue) View.ScrollToBlock(_vm.Timeline.BlockOf(cue));
    }

    private void OnHide(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) _vm.IsTimelineVisible = false;
    }
}
