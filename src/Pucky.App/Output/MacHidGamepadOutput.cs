using System.Diagnostics;
using Pucky.Core.Mapping;

namespace Pucky.App.Output;

/// <summary>
/// Sends gamepad reports to the native macOS HID helper. Keeping the helper in
/// a separate process prevents the restricted virtual-HID entitlement from
/// being applied to the CoreCLR apphost.
/// </summary>
public sealed class MacHidGamepadOutput(
    ILogger<MacHidGamepadOutput> logger) : IVirtualOutput
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(3);
    private readonly object _sync = new();
    private Process? _helper;
    private Stream? _helperInput;
    private Task<string>? _helperError;
    private DateTime _nextConnectAttempt;

    public string Name => "Stadia-compatible virtual HID gamepad";

    public bool IsConnected
    {
        get
        {
            lock (_sync)
            {
                return _helper is not null && !HasExited(_helper);
            }
        }
    }

    public string? UnavailableReason { get; private set; }

    public event Action<OutputFeedback>? FeedbackReceived
    {
        add { }
        remove { }
    }

    public bool Connect()
    {
        lock (_sync)
        {
            if (_helper is not null && !HasExited(_helper))
            {
                return true;
            }

            if (_helper is not null)
            {
                var exitCode = TryGetExitCode(_helper);
                var details = CompletedErrorText();
                DisconnectLocked();
                UnavailableReason = BuildExitReason(exitCode, details);
            }

            if (DateTime.UtcNow < _nextConnectAttempt)
            {
                return false;
            }
            _nextConnectAttempt = DateTime.UtcNow.AddSeconds(5);

            if (!OperatingSystem.IsMacOS())
            {
                UnavailableReason = "The macOS HID backend can only run on macOS.";
                return false;
            }

            var helperPath = Path.Combine(AppContext.BaseDirectory, "pucky-hid-helper");
            if (!File.Exists(helperPath))
            {
                UnavailableReason =
                    $"The native HID helper was not found at '{helperPath}'. " +
                    "Rebuild Pucky with scripts/build.sh on macOS.";
                return false;
            }

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = helperPath,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            try
            {
                if (!process.Start())
                {
                    process.Dispose();
                    UnavailableReason = "macOS did not start the native HID helper.";
                    return false;
                }

                var errorTask = process.StandardError.ReadToEndAsync();
                var readyTask = process.StandardOutput.ReadLineAsync();
                if (!readyTask.Wait(StartTimeout))
                {
                    StopProcess(process, process.StandardInput.BaseStream);
                    process.Dispose();
                    UnavailableReason =
                        "The native HID helper did not respond. Verify its code signature " +
                        "and the Mac's AMFI/SIP development configuration.";
                    return false;
                }

                var response = readyTask.GetAwaiter().GetResult();
                var expectedResponse = $"READY {MacHidGamepadReport.Length}";
                if (!string.Equals(response, expectedResponse, StringComparison.Ordinal))
                {
                    StopProcess(process, process.StandardInput.BaseStream);
                    var error = errorTask.IsCompletedSuccessfully
                        ? errorTask.Result.Trim()
                        : null;
                    process.Dispose();
                    UnavailableReason = response?.StartsWith("READY", StringComparison.Ordinal) == true
                        ? "The native HID helper uses an incompatible report format. " +
                          "Rebuild and replace the complete Pucky artifact."
                        : string.IsNullOrWhiteSpace(response)
                            ? BuildExitReason(null, error)
                            : response;
                    return false;
                }

                _helper = process;
                _helperInput = process.StandardInput.BaseStream;
                _helperError = errorTask;
                UnavailableReason = null;
                logger.LogInformation("Created the macOS virtual HID gamepad through the native helper");
                return true;
            }
            catch (Exception ex) when (
                ex is IOException or InvalidOperationException or UnauthorizedAccessException or
                    System.ComponentModel.Win32Exception or AggregateException)
            {
                StopProcess(process, TryGetStandardInput(process));
                process.Dispose();
                UnavailableReason = $"The native HID helper could not start: {ex.Message}";
                logger.LogWarning(ex, "Could not start the macOS virtual HID helper");
                return false;
            }
        }
    }

    public void Submit(VirtualGamepadState state)
    {
        lock (_sync)
        {
            if (_helper is null || _helperInput is null || HasExited(_helper))
            {
                return;
            }

            try
            {
                var report = MacHidGamepadReport.Encode(state);
                _helperInput.Write(report);
                _helperInput.Flush();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                UnavailableReason = $"The native HID helper stopped accepting reports: {ex.Message}";
                logger.LogWarning(ex, "The macOS virtual HID helper disconnected");
                DisconnectLocked();
            }
        }
    }

    public void Disconnect()
    {
        lock (_sync)
        {
            DisconnectLocked();
        }
    }

    public void Dispose() => Disconnect();

    private void DisconnectLocked()
    {
        var process = _helper;
        var input = _helperInput;
        _helper = null;
        _helperInput = null;
        _helperError = null;
        if (process is null)
        {
            input?.Dispose();
            return;
        }

        StopProcess(process, input);
        process.Dispose();
    }

    private string? CompletedErrorText() =>
        _helperError is { IsCompletedSuccessfully: true }
            ? _helperError.Result.Trim()
            : null;

    private static string BuildExitReason(int? exitCode, string? details)
    {
        var reason = exitCode is null
            ? "The native HID helper exited before creating a device."
            : $"The native HID helper exited with code {exitCode}.";
        return string.IsNullOrWhiteSpace(details) ? reason : $"{reason} {details}";
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

    private static int? TryGetExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static Stream? TryGetStandardInput(Process process)
    {
        try
        {
            return process.StandardInput.BaseStream;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static void StopProcess(Process process, Stream? input)
    {
        try
        {
            input?.Dispose();
            if (!process.HasExited && !process.WaitForExit(500))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(500);
            }
        }
        catch (Exception ex) when (
            ex is InvalidOperationException or NotSupportedException or IOException)
        {
        }
    }
}
