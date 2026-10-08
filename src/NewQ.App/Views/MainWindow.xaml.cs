using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using NewQ.App.Infrastructure;
using NewQ.App.ViewModels;

namespace NewQ.App.Views;

public partial class MainWindow : Window
{
    private readonly FieldCommit _fields;

    public MainWindow()
    {
        InitializeComponent();
        // Inspector fields confirm on Enter / click outside and give the keyboard back to the cue list (Space = GO).
        _fields = FieldCommit.Attach(this, field => !CueGrid.IsKeyboardFocusWithin, () => CueGrid.Focus());
        // Always open on the primary monitor: "CenterScreen" follows the mouse, and the control window could
        // end up on the projector, hidden behind a fullscreen (topmost) video output.
        var area = SystemParameters.WorkArea;
        Width = Math.Min(Width, area.Width);
        Height = Math.Min(Height, area.Height);
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + (area.Height - Height) / 2;
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    /// <summary>True while the user is typing somewhere, so show shortcuts must not fire.</summary>
    private static bool IsEditingText()
        => Keyboard.FocusedElement is TextBoxBase or PasswordBox
           || Keyboard.FocusedElement is ComboBox { IsEditable: true };

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+S and other shortcuts must see the value being typed (Enter / click outside: FieldCommit).
        if (_fields.EditedField() is TextBox field && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            FieldCommit.Commit(field);

        // PANIC must work everywhere, even while editing.
        if (e.Key == Key.Escape)
        {
            if (CueGrid.IsKeyboardFocusWithin && IsEditingText())
            {
                CueGrid.CancelEdit();
                return;
            }
            ViewModel.PanicCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (IsEditingText() || Keyboard.Modifiers != ModifierKeys.None) return;

        switch (e.Key)
        {
            case Key.Space:
                ViewModel.GoCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.P:
                ViewModel.TogglePauseCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.S:
                ViewModel.StopAllCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Delete when CueGrid.IsKeyboardFocusWithin:
                ViewModel.DeleteCueCommand.Execute(null);
                e.Handled = true;
                break;
        }
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
        if (CueGrid.SelectedItem is not null) CueGrid.ScrollIntoView(CueGrid.SelectedItem);
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
        if (ViewModel.IsSafeMode && MessageBox.Show("Modalità Safe attiva: vuoi davvero chiudere NewQ?\nTutte le cue in esecuzione verranno fermate.",
                "NewQ", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        if (!ViewModel.ConfirmDiscard()) e.Cancel = true;
    }
}
