using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace NewQ.App.Infrastructure;

/// <summary>
/// Text fields of a window confirm their value and give up the keyboard focus when the user presses Enter or
/// clicks anywhere outside them (also on areas that can't take focus). Shift+Enter still adds a line in
/// multi-line fields. Used by the main window (inspector) and the settings window.
/// </summary>
public sealed class FieldCommit
{
    private readonly Window _window;
    private readonly Func<TextBox, bool> _applies;
    private readonly Action _leave;

    /// <param name="applies">Which focused text fields are handled (e.g. not the cue list's cell editors).</param>
    /// <param name="leave">Where the focus goes after leaving a field.</param>
    private FieldCommit(Window window, Func<TextBox, bool> applies, Action leave)
    {
        _window = window;
        _applies = applies;
        _leave = leave;
        window.PreviewKeyDown += OnPreviewKeyDown;
        window.PreviewMouseDown += OnPreviewMouseDown;
    }

    public static FieldCommit Attach(Window window, Func<TextBox, bool>? applies = null, Action? leave = null)
        => new(window, applies ?? (_ => true), leave ?? (() => ClearFocus(window)));

    /// <summary>No element keeps the focus (a Window itself can't take it); keys still reach the window.</summary>
    private static void ClearFocus(Window window)
    {
        FocusManager.SetFocusedElement(window, null);
        Keyboard.ClearFocus();
    }

    /// <summary>The text field being edited, if it is one this window handles.</summary>
    public TextBox? EditedField()
        => Keyboard.FocusedElement is TextBox tb && _window.IsAncestorOf(tb) && _applies(tb) ? tb : null;

    /// <summary>Pushes the text being typed into its source (bindings normally update only on focus loss).</summary>
    public static void Commit(TextBox field)
    {
        field.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        if (ParentComboBox(field) is ComboBox combo)
            combo.GetBindingExpression(ComboBox.TextProperty)?.UpdateSource();
    }

    /// <summary>Confirms the field and moves the keyboard focus away (the field's LostFocus handlers run too).</summary>
    public void Leave(TextBox field)
    {
        Commit(field);
        _leave();
    }

    private static ComboBox? ParentComboBox(DependencyObject element)
        => element is FrameworkElement { TemplatedParent: ComboBox combo } ? combo : null;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || EditedField() is not TextBox field) return;
        if (field.AcceptsReturn && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        if (ParentComboBox(field) is { IsDropDownOpen: true }) return; // Enter picks the item
        Leave(field);
        e.Handled = true; // Enter confirms the field: it doesn't also press the dialog's default button
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (EditedField() is not TextBox field || field.IsMouseOver) return;
        if (ParentComboBox(field) is ComboBox { IsMouseOver: true } or ComboBox { IsDropDownOpen: true }) return;

        // If the click moves the focus somewhere else (another field, a button...) let it; otherwise leave the field.
        _window.Dispatcher.BeginInvoke(() =>
        {
            if (ReferenceEquals(Keyboard.FocusedElement, field)) Leave(field);
        }, DispatcherPriority.Input);
    }
}
