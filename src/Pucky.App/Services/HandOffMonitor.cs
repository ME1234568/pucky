using System.Diagnostics;
using Pucky.App.Profiles;

namespace Pucky.App.Services;

/// <summary>
/// Polls for configured processes (Steam by default) so Pucky can step aside
/// and let Steam Input or a game drive the controller natively.
/// </summary>
public sealed class HandOffMonitor(
    SettingsStore settings,
    ILogger<HandOffMonitor> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private volatile string? _activeProcess;

    /// <summary>The running process Pucky is yielding to, if any.</summary>
    public string? ActiveProcess => _activeProcess;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            var current = settings.Current;
            var found = current.HandOffEnabled ? FindRunning(current.HandOffProcesses) : null;
            if (found != _activeProcess)
            {
                logger.LogInformation(
                    found is null
                        ? "Hand-off process exited; Pucky is resuming control"
                        : "{Process} is running; handing the controller off",
                    found);
                _activeProcess = found;
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal static string? FindRunning(IReadOnlyCollection<string> names)
    {
        if (names.Count == 0)
        {
            return null;
        }

        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }

        try
        {
            foreach (var process in processes)
            {
                string processName;
                try
                {
                    processName = process.ProcessName;
                }
                catch (InvalidOperationException)
                {
                    continue;
                }

                foreach (var name in names)
                {
                    if (processName.Equals(name, StringComparison.OrdinalIgnoreCase))
                    {
                        return name;
                    }
                }
            }
            return null;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }
}
