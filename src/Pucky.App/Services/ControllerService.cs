using Pucky.App.Devices;
using Pucky.App.Output;
using Pucky.App.Profiles;
using Pucky.Core.Input;
using Pucky.Core.Mapping;

namespace Pucky.App.Services;

public sealed record PuckyStatus(
    bool Enabled,
    bool ControllerConnected,
    ControllerDeviceInfo? Device,
    bool VirtualControllerConnected,
    string Output,
    string? OutputError,
    string DesktopOutput,
    string ActiveProfile,
    long ReportsReceived,
    bool VibrationActive,
    string? LastError,
    ControllerState? LiveState);

public sealed record PuckyLiveFrame(long ReportsReceived, ControllerState? State);

public sealed class ControllerService(
    IControllerTransport transport,
    VirtualOutputFactory outputFactory,
    ProfileStore profiles,
    ILogger<ControllerService> logger) : BackgroundService
{
    private readonly MappingEngine _mapping = new();
    private readonly TrackpadHapticEngine _trackpadHaptics = new();
    private readonly IVirtualOutput _output = outputFactory.Create();
    private readonly IDesktopOutput _desktop = DesktopOutputFactory.Create();
    private readonly object _statusLock = new();
    private volatile bool _enabled = true;
    private ControllerState? _liveState;
    private long _reportsReceived;
    private string? _lastError;
    private OutputFeedback _gameRumble = new(0, 0);
    private OutputFeedback _testRumble = new(0, 0);
    private OutputFeedback _lastSentRumble = new(0, 0);
    private DateTime _testRumbleUntil = DateTime.MinValue;
    private DateTime _lastHeartbeat = DateTime.MinValue;
    private DateTime _lastRumble = DateTime.MinValue;

    public void Enable() => _enabled = true;

    public void Disable()
    {
        _enabled = false;
        _mapping.Reset();
    }

    public PuckyStatus GetStatus()
    {
        lock (_statusLock)
        {
            return new PuckyStatus(
                _enabled,
                transport.IsOpen,
                transport.Device,
                _output.IsConnected,
                _output.Name,
                _output.UnavailableReason,
                _desktop.Name,
                profiles.Active.Name,
                _reportsReceived,
                IsVibrationActive(),
                _lastError,
                _liveState);
        }
    }

    public PuckyLiveFrame GetLiveFrame()
    {
        lock (_statusLock)
        {
            return new PuckyLiveFrame(_reportsReceived, _liveState);
        }
    }

    public bool TestVibration()
    {
        if (!transport.IsOpen)
        {
            return false;
        }

        lock (_statusLock)
        {
            _testRumble = new OutputFeedback(210, 165);
            _testRumbleUntil = DateTime.UtcNow.AddMilliseconds(650);
            _lastRumble = DateTime.MinValue;
        }
        return SendRumble(_testRumble);
    }

    public bool TestTrackpadHaptics()
    {
        if (!transport.IsOpen)
        {
            return false;
        }

        var left = transport.SendTrackpadHapticPulse(
            TrackpadHapticSide.Left,
            1360,
            900,
            2);
        var right = transport.SendTrackpadHapticPulse(
            TrackpadHapticSide.Right,
            1360,
            900,
            2);
        return left && right;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Ensure hardware probing cannot delay the web host from starting.
        await Task.Yield();
        await profiles.InitializeAsync(stoppingToken);
        _output.FeedbackReceived += OnFeedback;
        var report = new byte[128];

        while (!stoppingToken.IsCancellationRequested)
        {
            if (!_enabled)
            {
                Deactivate();
                await Task.Delay(250, stoppingToken);
                continue;
            }

            if (!transport.IsOpen)
            {
                if (!transport.TryOpen())
                {
                    await Task.Delay(1000, stoppingToken);
                    continue;
                }
                _mapping.Reset();
                _trackpadHaptics.Reset();
                _lastError = null;
                if (!transport.DisableLizardMode())
                {
                    SetError("Controller opened, but lizard mode could not be disabled.");
                }
                _lastHeartbeat = DateTime.UtcNow;
            }

            if (!_output.IsConnected)
            {
                _output.Connect();
            }

            MaintainController();
            var count = transport.Read(report);
            if (count <= 0)
            {
                _desktop.Submit(
                    _mapping.ContinueDesktopMotion(
                        DateTimeOffset.UtcNow,
                        profiles.Active));
                await Task.Yield();
                continue;
            }

            if (!SteamControllerProtocol.TryParseState(
                    report.AsSpan(0, count),
                    out var state) ||
                state is null)
            {
                continue;
            }

            var mapped = _mapping.Map(state, profiles.Active);
            foreach (var pulse in _trackpadHaptics.Update(state, profiles.Active))
            {
                transport.SendTrackpadHapticPulse(
                    pulse.Side,
                    pulse.OnMicroseconds,
                    pulse.OffMicroseconds,
                    pulse.RepeatCount);
            }
            _output.Submit(mapped.Gamepad);
            _desktop.Submit(mapped.Desktop);
            lock (_statusLock)
            {
                _liveState = state;
                _reportsReceived++;
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        Deactivate();
        _output.FeedbackReceived -= OnFeedback;
        _desktop.Dispose();
        _output.Dispose();
        await transport.DisposeAsync();
    }

    private void MaintainController()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastHeartbeat).TotalMilliseconds >= 800)
        {
            transport.DisableLizardMode();
            _lastHeartbeat = now;
        }

        OutputFeedback effective;
        lock (_statusLock)
        {
            effective = now < _testRumbleUntil ? _testRumble : _gameRumble;
        }

        if ((effective.LargeMotor > 0 || effective.SmallMotor > 0) &&
            (now - _lastRumble).TotalMilliseconds >= 40)
        {
            SendRumble(effective);
            _lastRumble = now;
        }
        else if (effective.LargeMotor == 0 &&
                 effective.SmallMotor == 0 &&
                 (_lastSentRumble.LargeMotor > 0 || _lastSentRumble.SmallMotor > 0))
        {
            SendRumble(effective);
        }
    }

    private void Deactivate()
    {
        if (transport.IsOpen)
        {
            transport.SetRumble(0, 0);
            _lastSentRumble = new OutputFeedback(0, 0);
            transport.RestoreLizardMode();
        }
        _output.Disconnect();
        _trackpadHaptics.Reset();
        lock (_statusLock)
        {
            _liveState = null;
        }
    }

    private void OnFeedback(OutputFeedback feedback)
    {
        lock (_statusLock)
        {
            _gameRumble = feedback;
            _lastRumble = DateTime.MinValue;
        }

        // Stops are sent immediately so a game cannot leave a motor running.
        if (feedback.LargeMotor == 0 && feedback.SmallMotor == 0 &&
            DateTime.UtcNow >= _testRumbleUntil)
        {
            SendRumble(feedback);
        }
    }

    private bool SendRumble(OutputFeedback feedback)
    {
        var sent = transport.SetRumble(
            (ushort)(feedback.LargeMotor * 257),
            (ushort)(feedback.SmallMotor * 257));
        if (sent)
        {
            _lastSentRumble = feedback;
        }
        return sent;
    }

    private bool IsVibrationActive()
    {
        var effective = DateTime.UtcNow < _testRumbleUntil ? _testRumble : _gameRumble;
        return effective.LargeMotor > 0 || effective.SmallMotor > 0;
    }

    private void SetError(string error)
    {
        lock (_statusLock)
        {
            _lastError = error;
        }
        logger.LogWarning("{Error}", error);
    }
}
