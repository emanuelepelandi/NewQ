using System;
using System.Collections.Generic;
using System.Linq;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using NewQ.App.Infrastructure;
using NewQ.App.ViewModels;
using NewQ.Core.Input;

namespace NewQ.App.Views;

public partial class MainWindow : Window
{
    private readonly FieldCommit _fields;

    public MainWindow()
    {
        InitializeComponent();
        // Inspector fields confirm on Enter / click outside and give the keyboard back to the cue list (Space = GO).
        _fields = FieldCommit.Attach(this, field => !CueGrid.IsKeyboardFocusWithin, () => CueGrid.Focus());
        Loaded += (_, _) => HookView();
        // Always open on the primary monitor: "CenterScreen" follows the mouse, and the control window could
        // end up on the projector, hidden behind a fullscreen (topmost) video output.
        var area = SystemParameters.WorkArea;
        Width = Math.Min(Width, area.Width);
        Height = Math.Min(Height, area.Height);
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + (area.Height - Height) / 2;
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    // ------------------------------------------------------------------ view: list columns, panels

    private Dictionary<string, DataGridColumn> OptionalColumns() => new()
    {
        ["target"] = ColTarget, ["videoRoute"] = ColVideoRoute, ["audioRoute"] = ColAudioRoute, ["volume"] = ColVolume,
        ["opacity"] = ColOpacity, ["layer"] = ColLayer, ["prewait"] = ColPrewait, ["duration"] = ColDuration,
        ["postwait"] = ColPostwait, ["fadeIn"] = ColFadeIn, ["fadeOut"] = ColFadeOut, ["loop"] = ColLoop,
        ["continue"] = ColContinue, ["link"] = ColLink, ["armed"] = ColArmed, ["notes"] = ColNotes, ["state"] = ColState,
    };

    private void HookView()
    {
        ViewModel.Columns.PropertyChanged += (_, _) => ApplyColumns();
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsInspectorCollapsed)) ApplyInspector();
            if (e.PropertyName == nameof(MainViewModel.IsTimelineVisible)) ApplyTimeline();
            if (e.PropertyName == nameof(MainViewModel.IsRunningPanelVisible)) ApplyRunningPanel();
            if (e.PropertyName == nameof(MainViewModel.SelectedCue)) ShowInspectorPages();
        };
        ApplyColumns();
        ApplyInspector();
        ShowInspectorPages();
        ApplyRunningPanel();
        TimelineArea.Initialize(ViewModel);
        ApplyTimeline();
    }

    private void ApplyColumns()
    {
        foreach (var (key, column) in OptionalColumns())
            column.Visibility = ViewModel.Columns[key] ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Hides the properties panel at the bottom completely (the list gets the space), or opens it again. It comes back
    /// from Visualizza, its shortcut, or a right click on a cue.
    /// </summary>
    private void ApplyInspector()
    {
        var collapsed = ViewModel.IsInspectorCollapsed;
        if (collapsed)
        {
            if (InspectorPanel.IsKeyboardFocusWithin) CueGrid.Focus(); // never leave the focus in a hidden field
            if (InspectorRow.ActualHeight > 0) ViewModel.SaveInspectorHeight(InspectorRow.ActualHeight);
            InspectorRow.MinHeight = 0;
            InspectorRow.Height = new GridLength(0);
        }
        else
        {
            InspectorRow.MinHeight = 150;
            InspectorRow.Height = new GridLength(Math.Max(150, ViewModel.Settings.InspectorHeight));
        }
        InspectorSplitterRow.Height = new GridLength(collapsed ? 0 : 5);
        InspectorPanel.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        InspectorSplitter.Visibility = InspectorPanel.Visibility;
    }

    /// <summary>Shows or hides the "In esecuzione" panel on the right of the list (the list gets the width).</summary>
    private void ApplyRunningPanel()
    {
        var visible = ViewModel.IsRunningPanelVisible;
        RunningColumn.MinWidth = visible ? 280 : 0;
        RunningColumn.Width = new GridLength(visible ? Math.Max(280, ViewModel.Settings.RunningWidth) : 0);
        RunningSplitterColumn.Width = new GridLength(visible ? 5 : 0);
        RunningPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        RunningSplitter.Visibility = RunningPanel.Visibility;
    }

    /// <summary>A tab of the properties panel: a page of fields of the selected cue.</summary>
    public sealed class InspectorPage : Core.ObservableObject
    {
        private Core.Model.Cue _cue;

        public InspectorPage(string header, DataTemplate template, Core.Model.Cue cue)
        {
            Header = header;
            Template = template;
            _cue = cue;
        }

        public string Header { get; }
        public DataTemplate Template { get; }

        /// <summary>Changes in place when the next cue has the same pages: the fields are only rebound.</summary>
        public Core.Model.Cue Cue { get => _cue; set => SetField(ref _cue, value); }

        public override string ToString() => Header; // the tab's name for screen readers and UI automation
    }

    private List<InspectorPage> _pages = new();

    /// <summary>The pages of each cue type, in tab order (resource keys of MainWindow.xaml).</summary>
    private static (string Header, string Template)[] PagesOf(Core.Model.Cue cue) => cue switch
    {
        Core.Model.ShowMarkerCue => new[] { ("Base", "PageMarker") },
        Core.Model.AudioCue => Common(("Audio", "PageAudio"), ("Tempo e loop", "PageAudioTime")),
        Core.Model.VideoCue => Common(("Video", "PageVideo"), ("Layer", "PageDisplay"), ("Tempo e loop", "PageVideoTime")),
        Core.Model.ImageCue => Common(("Immagine", "PageImage"), ("Layer", "PageDisplay"), ("Tempo", "PageImageTime")),
        Core.Model.LiveCue => Common(("Sorgente", "PageLiveSource"), ("Uscita", "PageLiveOutput"), ("Layer", "PageDisplay")),
        Core.Model.MidiCue => Common(("MIDI", "PageMidi")),
        Core.Model.NetworkCue => Common(("Rete", "PageNetwork")),
        Core.Model.WaitCue => Common(("Attesa", "PageWait")),
        Core.Model.FadeCue => Common(("Fade", "PageFade")),
        Core.Model.StopCue => Common(("Stop", "PageStop")),
        _ => Common(),
    };

    private static (string Header, string Template)[] Common(params (string, string)[] own)
        => new[] { ("Base", "PageBase"), ("Trigger", "PageTrigger") }.Concat(own).ToArray();

    /// <summary>
    /// Tabs of the selected cue. A cue with the same pages (e.g. the next video, at every GO) reuses them: the fields
    /// are only rebound. Otherwise the open page stays open when the new cue has one with the same name.
    /// </summary>
    private void ShowInspectorPages()
    {
        if (_fields.EditedField() is TextBox field && InspectorPanel.IsAncestorOf(field)) FieldCommit.Commit(field);
        if (ViewModel.SelectedCue is not Core.Model.Cue cue)
        {
            _pages = new();
            InspectorTabs.ItemsSource = null;
            return;
        }
        var wanted = PagesOf(cue);
        if (wanted.Length == _pages.Count && wanted.Select(p => FindResource(p.Template)).SequenceEqual(_pages.Select(p => (object)p.Template)))
        {
            foreach (var page in _pages) page.Cue = cue;
            return;
        }
        var open = (InspectorTabs.SelectedItem as InspectorPage)?.Header;
        _pages = wanted.Select(p => new InspectorPage(p.Header, (DataTemplate)FindResource(p.Template), cue)).ToList();
        InspectorTabs.ItemsSource = _pages;
        InspectorTabs.SelectedItem = _pages.FirstOrDefault(p => p.Header == open) ?? _pages[0];
    }

    /// <summary>Right click on a cue: select it and open its properties.</summary>
    private void OnCueRowRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGridRow { Item: Core.Model.Cue cue }) return;
        ViewModel.SelectedCue = cue;
        ViewModel.IsInspectorCollapsed = false;
    }

    /// <summary>Shows the timeline under the list (at its saved height) or hides it completely.</summary>
    private void ApplyTimeline()
    {
        var visible = ViewModel.IsTimelineVisible;
        TimelineRow.MinHeight = visible ? 140 : 0;
        TimelineRow.Height = visible ? new GridLength(Math.Max(140, ViewModel.Settings.TimelineHeight)) : new GridLength(0);
        TimelineSplitterRow.Height = new GridLength(visible ? 5 : 0);
        TimelineArea.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        TimelineSplitter.Visibility = TimelineArea.Visibility;
    }

    private void OnTimelineResized(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (ViewModel.IsTimelineVisible) ViewModel.SaveTimelineSettings(s => s.TimelineHeight = TimelineRow.ActualHeight);
    }

    private void OnInspectorResized(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (!ViewModel.IsInspectorCollapsed) ViewModel.SaveInspectorHeight(InspectorRow.ActualHeight);
    }

    private void OnRunningResized(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (ViewModel.IsRunningPanelVisible) ViewModel.SaveRunningWidth(RunningColumn.ActualWidth);
    }

    /// <summary>True while the user is typing somewhere, so show shortcuts must not fire.</summary>
    private static bool IsEditingText()
        => Keyboard.FocusedElement is TextBoxBase or PasswordBox
           || Keyboard.FocusedElement is ComboBox { IsEditable: true };

    /// <summary>Controls that use the arrow keys themselves: text, lists, sliders, menus, the geometry editor.</summary>
    private static bool ArrowsBelongToFocus()
        => Keyboard.FocusedElement is TextBoxBase or PasswordBox or ComboBox or ComboBoxItem or Slider or ScrollBar
               or MenuItem or ListBoxItem or Controls.GeometryEditor
           || Keyboard.FocusedElement is DependencyObject d && ItemsControl.ItemsControlFromItemContainer(d) is ComboBox;

    /// <summary>
    /// Keyboard shortcuts come from the editable map (Strumenti → Scorciatoie). While typing in a field only
    /// gestures with Ctrl/Alt and Esc work, so typing never fires GO or Stop.
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ShortcutKeys.FromEvent(e) is not string gesture) return;

        // Ctrl+S and other shortcuts must see the value being typed (Enter / click outside: FieldCommit).
        if (_fields.EditedField() is TextBox field && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            FieldCommit.Commit(field);

        // Esc first cancels an edit in the cue list.
        if (gesture == "Escape" && CueGrid.IsKeyboardFocusWithin && IsEditingText())
        {
            CueGrid.CancelEdit();
            return;
        }

        if (ViewModel.FindShortcut(gesture) is not ShortcutAction action)
        {
            // Up/Down move the playhead from anywhere in the window, not only from the list (elsewhere WPF would
            // walk the focus through the controls instead).
            if (gesture is "Up" or "Down" && !ArrowsBelongToFocus())
            {
                ViewModel.Engine.MovePlayhead(gesture == "Up" ? -1 : 1);
                if (!CueGrid.IsKeyboardFocusWithin) CueGrid.Focus();
                e.Handled = true;
            }
            return;
        }
        var whileTyping = ShortcutMap.HasCommandModifier(gesture) || action == ShortcutAction.Panic; // PANIC works everywhere
        if (IsEditingText() && !whileTyping) return;
        if (action == ShortcutAction.Delete && !CueGrid.IsKeyboardFocusWithin) return; // never delete from another control

        var command = ViewModel.CommandFor(action);
        if (command.CanExecute(null)) command.Execute(null);
        e.Handled = true;
    }

    // ------------------------------------------------------------------ In esecuzione: scrubbing a paused cue

    private void OnRunningBarDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: Core.Model.Cue { CanSeek: true } } bar) return;
        bar.CaptureMouse();
        SeekTo(bar, e);
        e.Handled = true;
    }

    private void OnRunningBarMove(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { IsMouseCaptured: true } bar) SeekTo(bar, e);
    }

    private void OnRunningBarUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { IsMouseCaptured: true } bar) bar.ReleaseMouseCapture();
    }

    private void SeekTo(FrameworkElement bar, MouseEventArgs e)
    {
        if (bar.DataContext is not Core.Model.Cue { CanSeek: true } cue || bar.ActualWidth <= 0) return;
        ViewModel.SeekCue(cue, Math.Clamp(e.GetPosition(bar).X / bar.ActualWidth, 0, 1));
    }

    private void OnCueGridSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CueGrid.SelectedItem is not { } item) return;
        CueGrid.ScrollIntoView(item);
        // The list's current cell follows the playhead (GO, arrows, remote): Enter/F2 then edit the right row.
        if (!ReferenceEquals(CueGrid.CurrentItem, item) && !IsEditingText())
            CueGrid.CurrentCell = new DataGridCellInfo(item, CueGrid.CurrentCell.Column ?? CueGrid.Columns[0]);
    }

    /// <summary>--SHOW START-- / --SHOW END-- keep their fixed number and name.</summary>
    private void OnCueGridBeginningEdit(object sender, DataGridBeginningEditEventArgs e)
    {
        if (e.Row.Item is Core.Model.Cue { IsShowMarker: true }) e.Cancel = true;
    }

    private void OnCueGridDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            ViewModel.AddFiles(files);
    }

    // The route list arrives (RelativeSource binding) after the selected id: show the id again once loaded.
    private void OnRoutePickerLoaded(object sender, RoutedEventArgs e)
    {
        var combo = (ComboBox)sender;
        combo.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle,
            () => combo.GetBindingExpression(Selector.SelectedValueProperty)?.UpdateTarget());
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e) => OpenSettings(0);

    /// <summary>Status bar: a click copies the web remote's address (to send it to a phone).</summary>
    private void OnWebAddressClick(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel.WebAddresses.FirstOrDefault() is not string address) return;
        try { Clipboard.SetText(address); ViewModel.ReportStatus($"Indirizzo copiato: {address}"); }
        catch { /* clipboard busy */ }
    }

    private void OnSetupTabClick(object sender, RoutedEventArgs e)
        => OpenSettings(int.Parse((string)((FrameworkElement)sender).Tag, System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Opens the settings on a tab: 0 general, 1 control, 2 audio routes, 3 video routes, 4 video tests, 5 audio tests, 6 shortcuts.</summary>
    public void OpenSettings(int tab)
    {
        if (!ViewModel.CanEdit) return; // Safe mode: no device or route changes during the show
        var dialog = new SettingsWindow(ViewModel.Settings.Clone(), ViewModel.Workspace.Settings, ViewModel.MidiDevices,
            ViewModel.CreateSetupContext(), tab) { Owner = this };
        bool? result;
        try { result = dialog.ShowDialog(); }
        finally { ViewModel.StopTestSignals(); } // test tones and patterns never outlive the setup window
        if (result == true)
            ViewModel.ApplySettings(dialog.Result);
    }

    private void OnExitClick(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // During the show, closing NewQ must be a deliberate choice.
        if (ViewModel.IsSafeMode && MessageBox.Show("Modalità Safe attiva: chiudere NewQ?",
                "NewQ", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        if (!ViewModel.ConfirmDiscard()) e.Cancel = true;
    }
}
