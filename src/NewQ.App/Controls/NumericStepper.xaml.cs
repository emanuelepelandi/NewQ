using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace NewQ.App.Controls;

/// <summary>
/// Numeric field with −/+ buttons (replaces ModernWpf's NumberBox, whose inner parts don't follow NewQ's form
/// styles). Typing commits on Enter or focus loss; ↑/↓ step by <see cref="Step"/>, Shift ×10.
/// </summary>
public partial class NumericStepper : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(NumericStepper),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((NumericStepper)d).ShowValue()));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(NumericStepper), new PropertyMetadata(double.MinValue, (d, _) => ((NumericStepper)d).ShowValue()));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(NumericStepper), new PropertyMetadata(double.MaxValue, (d, _) => ((NumericStepper)d).ShowValue()));

    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(
        nameof(Step), typeof(double), typeof(NumericStepper), new PropertyMetadata(1.0));

    public static readonly DependencyProperty DecimalsProperty = DependencyProperty.Register(
        nameof(Decimals), typeof(int), typeof(NumericStepper), new PropertyMetadata(0, (d, _) => ((NumericStepper)d).ShowValue()));

    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
        nameof(Header), typeof(string), typeof(NumericStepper), new PropertyMetadata(null));

    public NumericStepper()
    {
        InitializeComponent();
        // handledEventsToo: pages that commit text fields on Enter mark the key handled before it gets here.
        Box.AddHandler(PreviewKeyDownEvent, new KeyEventHandler(OnBoxKeyDown), handledEventsToo: true);
        ShowValue();
    }

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Step { get => (double)GetValue(StepProperty); set => SetValue(StepProperty, value); }
    public int Decimals { get => (int)GetValue(DecimalsProperty); set => SetValue(DecimalsProperty, value); }
    public string? Header { get => (string?)GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }

    private void ShowValue()
    {
        if (Box is null) return;
        Box.Text = Math.Round(Value, Decimals).ToString("F" + Decimals, CultureInfo.CurrentCulture);
        DownButton.IsEnabled = Value > Minimum;
        UpButton.IsEnabled = Value < Maximum;
    }

    private void Commit(double value)
    {
        value = Math.Clamp(Math.Round(value, Decimals), Minimum, Maximum);
        Value = value;
        GetBindingExpression(ValueProperty)?.UpdateSource();
        ShowValue(); // also when the value didn't change (restores a bad entry)
    }

    private void CommitText()
    {
        if (double.TryParse(Box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var typed) && double.IsFinite(typed))
            Commit(typed);
        else
            ShowValue();
    }

    private void OnBoxLostFocus(object sender, RoutedEventArgs e) => CommitText();

    private void OnBoxKeyDown(object sender, KeyEventArgs e)
    {
        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? Step * 10 : Step;
        switch (e.Key)
        {
            case Key.Enter: CommitText(); Box.SelectAll(); break;
            case Key.Up: Commit(Value + step); break;
            case Key.Down: Commit(Value - step); break;
            default: return;
        }
        e.Handled = true;
    }

    private void OnUp(object sender, RoutedEventArgs e) => Commit(Value + Step);
    private void OnDown(object sender, RoutedEventArgs e) => Commit(Value - Step);
}
