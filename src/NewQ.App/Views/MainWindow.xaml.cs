using System;
using System.Collections.Generic;
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

    // ------------------------------------------------------------------ view: list columns, properties panel

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
        };
        ApplyColumns();
        ApplyInspector();
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
    /// Hides the properties panel completely (the list gets the space), or opens it again. It comes back from
    /// Visualizza, its shortcut, or a right click on a cue.
    /// </summary>
    private void ApplyInspector()
    {
        var collapsed = ViewModel.IsInspectorCollapsed;
        if (collapsed)
        {
            if (InspectorPanel.IsKeyboardFocusWithin) CueGrid.Focus(); // never leave the focus in a hidden field
            if (InspectorColumn.ActualWidth > 0) ViewModel.SaveInspectorWidth(InspectorColumn.ActualWidth);
            InspectorColumn.MinWidth = 0;
            InspectorColumn.Width = new GridLength(0);
        }
        else
        {
            InspectorColumn.MinWidth = 320;
            InspectorColumn.Width = new GridLength(Math.Max(320, ViewModel.Settings.InspectorWidth));
        }
        InspectorSplitterColumn.Width = new GridLength(collapsed ? 0 : 5);
        InspectorPanel.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        InspectorSplitter.Visibility = InspectorPanel.Visibility;
    }

    /// <summary>Shows or hides the "In esecuzione" / "Log" panel under the list (the list gets the height).</summary>
    private void ApplyRunningPanel()
    {
        var visible = ViewModel.IsRunningPanelVisible;
        RunningRow.MinHeight = visible ? 90 : 0;
        RunningRow.Height = visible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        RunningSplitterRow.Height = new GridLength(visible ? 5 : 0);
        RunningPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        RunningSplitter.Visibility = RunningPanel.Visibility;
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
        if (!ViewModel.IsInspectorCollapsed) ViewModel.SaveInspectorWidth(InspectorColumn.ActualWidth);
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

    // ------------------------------------------------------------------ timeline scrubbing

    private bool _scrubbing;

    private void OnScrubStart(object sender, MouseButtonEventArgs e) => _scrubbing = true;

    private void OnScrubEnd(object sender, RoutedEventArgs e) => _scrubbing = false;

    /// <summary>Only user-driven changes seek; the periodic progress updates also change the value.</summary>
    private void OnScrubValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_scrubbing && sender is Slider { DataContext: Core.Model.Cue cue })
            ViewModel.SeekCue(cue, e.NewValue);
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

    private void OnSetupTabClick(object sender, RoutedEventArgs e)
        => OpenSettings(int.Parse((string)((FrameworkElement)sender).Tag, System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Opens the settings on a tab: 0 general, 1 audio routes, 2 video routes, 3 video tests, 4 audio tests.</summary>
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
