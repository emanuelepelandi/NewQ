using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using NewQ.App.Controls;
using NewQ.App.Infrastructure;
using NewQ.Core;
using NewQ.Core.Input;

namespace NewQ.App.Views.Setup;

/// <summary>Editor of the keyboard shortcuts. Works on a copy: the settings window applies it on OK.</summary>
public partial class ShortcutsPanel : UserControl
{
    public sealed class Row : ObservableObject
    {
        private string _gesture;

        public Row(ShortcutInfo info, string gesture, bool firstOfGroup)
        {
            Info = info;
            _gesture = gesture;
            GroupHeader = info.Group.ToUpperInvariant();
            GroupHeaderVisibility = firstOfGroup ? Visibility.Visible : Visibility.Collapsed;
        }

        public ShortcutInfo Info { get; }
        public string Label => Info.Label;
        public string GroupHeader { get; }
        public Visibility GroupHeaderVisibility { get; }
        public string Gesture { get => _gesture; set => SetField(ref _gesture, value); }
    }

    private ShortcutMap _map = new();
    private List<Row> _rows = new();

    public ShortcutsPanel() => InitializeComponent();

    /// <summary>The edited shortcuts.</summary>
    public ShortcutMap Map => _map;

    public void Initialize(ShortcutMap current)
    {
        _map = current.Clone();
        string? group = null;
        _rows = ShortcutMap.Catalog.Select(info =>
        {
            var row = new Row(info, _map[info.Action], info.Group != group);
            group = info.Group;
            return row;
        }).ToList();
        Rows.ItemsSource = _rows;
    }

    private void Refresh()
    {
        foreach (var row in _rows) row.Gesture = _map[row.Info.Action];
    }

    private void Assign(Row row, string gesture)
    {
        var lost = _map.Assign(row.Info.Action, gesture);
        Notice.Text = lost is ShortcutAction other
            ? $"{ShortcutKeys.Display(gesture)} era assegnato a «{ShortcutMap.Catalog.First(i => i.Action == other).Label}», che ora non ha tasto."
            : "";
        Refresh();
    }

    private void OnCaptured(object? sender, System.EventArgs e)
    {
        if (sender is KeyCaptureBox { DataContext: Row row } box) Assign(row, box.Gesture);
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is Row row) Assign(row, "");
    }

    private void OnResetOne(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is Row row) Assign(row, row.Info.DefaultGesture);
    }

    private void OnResetAll(object sender, RoutedEventArgs e)
    {
        _map.ResetToDefaults();
        Notice.Text = "";
        Refresh();
    }
}
