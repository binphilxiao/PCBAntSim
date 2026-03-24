using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace AntennaSimulatorApp;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            File.AppendAllText("crash.log",
                $"\n[{DateTime.Now}] AppDomain unhandled:\n{args.ExceptionObject}\n");
        };
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        File.AppendAllText("crash.log",
            $"\n[{DateTime.Now}] Dispatcher unhandled:\n{e.Exception}\n");
        MessageBox.Show(e.Exception.ToString(), "Unhandled Exception", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}

