using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using NewQ.App.ViewModels;

namespace NewQ.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    /// <summary>True while the user is typing somewhere, so show shortcuts must not fire.</summary>
    private static bool IsEditingText()
        => Keyboard.FocusedElement is TextBoxBase or PasswordBox
           || Keyboard.FocusedElement is ComboBox { IsEditable: true };

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
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

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(ViewModel.Settings.Clone(), ViewModel.Workspace.Settings, ViewModel.MidiDevices) { Owner = this };
        if (dialog.ShowDialog() == true)
            ViewModel.ApplySettings(dialog.Result);
    }

    private void OnExitClick(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!ViewModel.ConfirmDiscard()) e.Cancel = true;
    }
}
