using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.DualShock4;
using Pucky.Core.Mapping;

namespace Pucky.App.Output;

/// <summary>
/// A ViGEmBus DualShock 4 whose extended report carries the Steam
/// Controller's gyroscope and accelerometer, so games and emulators that
/// read PlayStation motion data receive gyro aiming.
/// </summary>
public sealed class WindowsDualShock4Output(
    ILogger<WindowsDualShock4Output> logger) : IVirtualOutput
{
    private readonly byte[] _report = new byte[DualShock4Report.Length];
    private ViGEmClient? _client;
    private IDualShock4Controller? _controller;
    private DateTimeOffset _lastReport;
    private ushort _timestamp;

    public string Name => "DualShock 4 with gyro (ViGEmBus)";
    public bool IsConnected => _controller is not null;
    public string? UnavailableReason { get; private set; }
    public event Action<OutputFeedback>? FeedbackReceived;

    public bool Connect()
    {
        if (_controller is not null)
        {
            return true;
        }

        try
        {
            _client = new ViGEmClient();
            _controller = _client.CreateDualShock4Controller();
            _controller.AutoSubmitReport = false;
            // FeedbackReceived is marked obsolete in favor of raw output
            // reports, but still reports motor levels and is all Pucky needs.
#pragma warning disable CS0618
            _controller.FeedbackReceived += OnFeedback;
#pragma warning restore CS0618
            _controller.Connect();
            UnavailableReason = null;
            return true;
        }
        catch (Exception ex)
        {
            UnavailableReason =
                "ViGEmBus is not installed or could not be opened. Install ViGEmBus, then restart Pucky.";
            logger.LogWarning(ex, "Could not create the virtual DualShock 4 controller");
            Disconnect();
            return false;
        }
    }

    public void Submit(VirtualGamepadState state)
    {
        var controller = _controller;
        if (controller is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (_lastReport != default)
        {
            // DS4 timestamps advance in units of 16/3 microseconds.
            var elapsedMicroseconds = Math.Clamp((now - _lastReport).TotalMicroseconds, 0, 100_000);
            _timestamp = unchecked((ushort)(_timestamp + (ushort)(elapsedMicroseconds * 3 / 16)));
        }
        _lastReport = now;

        DualShock4Report.Encode(state, _timestamp, _report);
        try
        {
            controller.SubmitRawReport(_report);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "DualShock 4 report submission failed");
        }
    }

    public void Disconnect()
    {
        if (_controller is not null)
        {
            try
            {
#pragma warning disable CS0618
                _controller.FeedbackReceived -= OnFeedback;
#pragma warning restore CS0618
                _controller.Disconnect();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Error while disconnecting ViGEm DualShock 4 target");
            }
            _controller = null;
        }

        _client?.Dispose();
        _client = null;
        _lastReport = default;
    }

    public void Dispose() => Disconnect();

    private void OnFeedback(object? sender, DualShock4FeedbackReceivedEventArgs e) =>
        FeedbackReceived?.Invoke(new OutputFeedback(e.LargeMotor, e.SmallMotor));
}
