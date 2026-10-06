using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using NewQ.App.Infrastructure;
using NewQ.App.ViewModels;
using NewQ.App.Views;

namespace NewQ.App;

public partial class App : Application
{
    private MainViewModel? _viewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 1 ms timer resolution: pre/post-waits and fades fire on time.
        NativeMethods.timeBeginPeriod(1);

        // Let bindings parse numbers with the user's locale (e.g. "1,5" in Italian).
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag)));

        DispatcherUnhandledException += OnUnhandledException;

        _viewModel = new MainViewModel(Dispatcher);
        var window = new MainWindow { DataContext = _viewModel };
        MainWindow = window;
        window.Show();

        var file = e.Args.FirstOrDefault(a => a.EndsWith(Core.Model.Workspace.FileExtension, StringComparison.OrdinalIgnoreCase));
        if (file is not null) _viewModel.OpenFile(file);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _viewModel?.Dispose();
        NativeMethods.timeEndPeriod(1);
        base.OnExit(e);
    }

    /// <summary>
    /// During a show the app must keep running: log the error instead of crashing, and never open a modal
    /// dialog that would block GO.
    /// </summary>
    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (_viewModel is not null)
            _viewModel.ReportUnhandled(e.Exception);
        else
            MessageBox.Show($"Errore imprevisto:\n{e.Exception.Message}", "NewQ", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
