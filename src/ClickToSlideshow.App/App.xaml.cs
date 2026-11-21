using System.Text;
using ClickToSlideshow.App.Diagnostics;
using ClickToSlideshow.Core.Audio;
using Microsoft.UI.Xaml;
using Microsoft.Windows.ApplicationModel.Resources;

namespace ClickToSlideshow.App;

public partial class App : Application
{
    private static readonly ResourceLoader ResourceLoader = new();

    private Window? _window;

    public App()
    {
        InitializeComponent();
        AppLog.PruneOldLogs();
        OggAudioTranscoder.PruneStaleCache(TimeSpan.FromDays(7));
        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
    }

    // Fires on the UI thread for XAML/binding/most app-code exceptions. Handled = true keeps the
    // process alive rather than terminating immediately — deliberate: this is an editing session
    // (slide order, rotations, undo history), and losing all of it to a silent crash from one bad
    // code path is worse than surfacing a message and letting the user keep going. AppDomain's
    // handler below covers the case where that trade-off isn't available at all.
    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        AppLog.LogError("UnhandledException", e.Exception);
        e.Handled = true;

        try
        {
            if (_window is MainWindow mainWindow)
            {
                mainWindow.ViewModel.ShowError(ResourceLoader.GetString("UnexpectedErrorMessage"));
            }
        }
        catch (Exception)
        {
            // Best-effort — the log write above already captured what matters.
        }
    }

    // Background-thread exceptions land here instead — by the time this fires the process is
    // already terminating (IsTerminating is true in practice on .NET), so this is purely
    // "log before you die", not a place to attempt any UI recovery.
    private void OnAppDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            AppLog.LogError("AppDomainUnhandledException", ex);
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Unpackaged app — command-line args come through Environment, not LaunchActivatedEventArgs.
        // args[0] is the exe path. `--list <path> [--skipped <path>]` is the real shell
        // extension's launch contract (M6), the same .list temp-file IPC M1's Cli already uses;
        // the bare single-folder-path form is a dev-launch convenience for running the app
        // directly (e.g. `dotnet run`) without going through the shell extension.
        string[] cmdArgs = Environment.GetCommandLineArgs();

        string? listPath = FindArgValue(cmdArgs, "--list");
        if (listPath is not null)
        {
            string? skippedPath = FindArgValue(cmdArgs, "--skipped");
            string[] filePaths = ReadAndDeleteTempFile(listPath);
            string[] skippedNames = skippedPath is not null ? ReadAndDeleteTempFile(skippedPath) : [];

            _window = new MainWindow(filePaths, skippedNames);
        }
        else
        {
            string? folderPath = cmdArgs.Length > 1 ? cmdArgs[1] : null;
            _window = new MainWindow(folderPath);
        }

        _window.Activate();
    }

    private static string? FindArgValue(string[] cmdArgs, string name)
    {
        int index = Array.IndexOf(cmdArgs, name);
        return (index >= 0 && index + 1 < cmdArgs.Length) ? cmdArgs[index + 1] : null;
    }

    private static string[] ReadAndDeleteTempFile(string path)
    {
        string[] lines = File.ReadAllLines(path, Encoding.UTF8);
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort cleanup — a leftover temp file is untidy but harmless.
        }

        return lines;
    }
}
