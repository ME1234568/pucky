using System.Diagnostics;
using System.Runtime.InteropServices;
using Pucky.App.Profiles;

namespace Pucky.App.Services;

public sealed class UiWindowLauncher(
    IHostApplicationLifetime lifetime,
    IConfiguration configuration,
    SettingsStore settings,
    ILogger<UiWindowLauncher> logger) : IHostedService
{
    private readonly object _gate = new();
    private ManagedBrowserWindow? _window;

    /// <summary>True when started with --background or PUCKY_NO_WINDOW=1.</summary>
    public static bool LaunchedInBackground { get; set; }

    public string Url
    {
        get
        {
            var configuredUrl = configuration["PUCKY_URL"] ?? "http://127.0.0.1:27182";
            return configuredUrl.Split(';', StringSplitOptions.RemoveEmptyEntries)[0]
                .Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal)
                .Replace("[::]", "127.0.0.1", StringComparison.Ordinal)
                .Replace("+", "127.0.0.1", StringComparison.Ordinal)
                .Replace("*", "127.0.0.1", StringComparison.Ordinal);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Environment.GetEnvironmentVariable("PUCKY_NO_WINDOW") == "1" ||
            LaunchedInBackground ||
            settings.Current.StartInBackground)
        {
            logger.LogInformation("Pucky is running in the background at {Url}", Url);
            return Task.CompletedTask;
        }

        lifetime.ApplicationStarted.Register(() => OpenWindow());
        return Task.CompletedTask;
    }

    /// <summary>
    /// Opens the control window, or brings the existing one forward.
    /// </summary>
    public void OpenWindow()
    {
        lock (_gate)
        {
            var existing = _window;
            if (existing is { OwnsProcess: true } && !HasExited(existing.Process))
            {
                if (TryFocus(existing.Process))
                {
                    return;
                }
            }
            else if (existing is not null)
            {
                existing.Process.Dispose();
            }

            _window = OpenAppWindow(Url, logger);
        }
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static bool TryFocus(Process process)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            process.Refresh();
            var handle = process.MainWindowHandle;
            if (handle == IntPtr.Zero)
            {
                return false;
            }
            ShowWindow(handle, 9 /* SW_RESTORE */);
            return SetForegroundWindow(handle);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        ManagedBrowserWindow? window;
        lock (_gate)
        {
            window = _window;
            _window = null;
        }
        if (window is null)
        {
            return;
        }

        if (!window.OwnsProcess)
        {
            window.Process.Dispose();
            return;
        }

        try
        {
            if (!window.Process.HasExited)
            {
                window.Process.CloseMainWindow();
                await window.Process.WaitForExitAsync(
                    cancellationToken.IsCancellationRequested
                        ? CancellationToken.None
                        : cancellationToken).WaitAsync(TimeSpan.FromMilliseconds(900));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or OperationCanceledException)
        {
        }

        try
        {
            if (!window.Process.HasExited)
            {
                window.Process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            window.Process.Dispose();
        }
    }

    internal static ManagedBrowserWindow? OpenAppWindow(string url, ILogger logger)
    {
        var separator = url.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        var windowUrl = $"{url}{separator}puckyWindow=1&launch={Environment.ProcessId}";
        var profileDirectory = Path.Combine(
            Path.GetTempPath(),
            "Pucky",
            $"browser-{Environment.ProcessId}");

        foreach (var browser in BrowserCandidates())
        {
            try
            {
                var start = new ProcessStartInfo
                {
                    FileName = browser,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Normal
                };
                start.ArgumentList.Add($"--app={windowUrl}");
                start.ArgumentList.Add("--new-window");
                start.ArgumentList.Add($"--user-data-dir={profileDirectory}");
                start.ArgumentList.Add("--no-first-run");
                start.ArgumentList.Add("--disable-background-mode");
                var process = Process.Start(start);
                if (process is not null)
                {
                    logger.LogInformation("Opened Pucky control window with {Browser}", browser);
                    return new ManagedBrowserWindow(process, OwnsProcess: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                logger.LogDebug(ex, "App-window browser {Browser} is unavailable", browser);
            }
        }

        try
        {
            return Process.Start(new ProcessStartInfo
            {
                FileName = windowUrl,
                UseShellExecute = true
            }) is { } fallback
                ? new ManagedBrowserWindow(fallback, OwnsProcess: false)
                : null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not open the Pucky control window at {Url}", url);
            return null;
        }
    }

    internal sealed record ManagedBrowserWindow(Process Process, bool OwnsProcess);

    private static IEnumerable<string> BrowserCandidates()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Microsoft", "Edge", "Application", "msedge.exe");
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Microsoft", "Edge", "Application", "msedge.exe");
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "Edge", "Application", "msedge.exe");
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Google", "Chrome", "Application", "chrome.exe");
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Google", "Chrome", "Application", "chrome.exe");
            yield return "msedge.exe";
            yield return "chrome.exe";
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
            yield return "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge";
        }
        else if (OperatingSystem.IsLinux())
        {
            yield return "google-chrome";
            yield return "chromium";
            yield return "chromium-browser";
        }
    }
}
