using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using NewQ.App.ViewModels;
using NewQ.Core.Check;
using NewQ.Core.Model;

namespace NewQ.App.Views;

public partial class ShowCheckWindow : Window
{
    public sealed record IssueRow(CheckIssue Issue)
    {
        public string SeverityLabel => Issue.Severity switch
        {
            CheckSeverity.Error => "ERRORE",
            CheckSeverity.Warning => "AVVISO",
            _ => "INFO",
        };

        // Semantic colours from the design tokens (Themes/Tokens.xaml).
        public Brush BadgeForeground => Token(Issue.Severity switch
        {
            CheckSeverity.Error => "ErrorBrush",
            CheckSeverity.Warning => "WarningBrush",
            _ => "InfoBrush",
        });

        public Brush BadgeBackground => Token(Issue.Severity switch
        {
            CheckSeverity.Error => "ErrorTintBrush",
            CheckSeverity.Warning => "WarningTintBrush",
            _ => "InfoTintBrush",
        });

        public string CueLabel => Issue.Cue is Cue cue ? $"{cue.Number}  {cue.Name}".Trim() : "Workspace";
        public string Message => Issue.Message;
    }

    private static Brush Token(string key) => (Brush)Application.Current.FindResource(key);

    private readonly MainViewModel _viewModel;
    private IReadOnlyList<CheckIssue> _issues = Array.Empty<CheckIssue>();
    private CancellationTokenSource? _cts;

    public ShowCheckWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        Loaded += (_, _) => Run();
        Closed += (_, _) => _cts?.Cancel();
    }

    public async void Run()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        RerunButton.IsEnabled = false;
        SummaryText.Text = "Controllo in corso…";
        Progress.Value = 0;
        Progress.Visibility = Visibility.Visible;
        EmptyText.Visibility = Visibility.Collapsed;
        IssueList.ItemsSource = null;

        var clock = Stopwatch.StartNew();
        try
        {
            _issues = await ShowChecker.RunAsync(_viewModel.Workspace, _viewModel.CreateCheckEnvironment(),
                new Progress<double>(p => Progress.Value = p), token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            SummaryText.Text = $"Controllo interrotto: {ex.Message}";
            RerunButton.IsEnabled = true;
            return;
        }

        var errors = _issues.Count(i => i.Severity == CheckSeverity.Error);
        var warnings = _issues.Count(i => i.Severity == CheckSeverity.Warning);
        var infos = _issues.Count(i => i.Severity == CheckSeverity.Info);
        var cues = _viewModel.Workspace.Cues.Count;
        SummaryText.Text = errors + warnings == 0
            ? $"Tutto in ordine · {cues} cue controllate in {clock.Elapsed.TotalSeconds:0.0} s"
            : $"{errors} errori · {warnings} avvisi · {infos} informazioni — {cues} cue controllate in {clock.Elapsed.TotalSeconds:0.0} s";
        SummaryText.Foreground = errors > 0
            ? (Brush)FindResource("ErrorBrush")
            : warnings > 0 ? (Brush)FindResource("InactiveBrush") : (Brush)FindResource("RunningBrush");
        Progress.Visibility = Visibility.Collapsed;
        RerunButton.IsEnabled = true;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var rows = _issues
            .Where(i => ShowInfoBox.IsChecked == true || i.Severity != CheckSeverity.Info)
            .Select(i => new IssueRow(i))
            .ToList();
        IssueList.ItemsSource = rows;
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) ApplyFilter();
    }

    private void OnIssueClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && sender is FrameworkElement { DataContext: IssueRow { Issue.Cue: Cue cue } })
            _viewModel.SelectedCue = cue;
    }

    private void OnRerun(object sender, RoutedEventArgs e) => Run();

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
