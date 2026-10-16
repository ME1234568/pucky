using System.Reflection;
using Pucky.App.Devices;
using Pucky.App.Output;
using Pucky.App.Profiles;
using Pucky.Core.Input;
using Pucky.Core.Mapping;

namespace Pucky.App.Services;

public sealed record PuckyStatus(
    string Version,
    bool Enabled,
    string? PausedReason,
    bool ControllerConnected,
    ControllerDeviceInfo? Device,
    bool VirtualControllerConnected,
    string Output,
    GamepadOutputType OutputType,
    IReadOnlyList<GamepadOutputType> SupportedOutputTypes,
    string? OutputError,
    string DesktopOutput,
    string ActiveProfile,
    string ActiveProfileId,
    long ReportsReceived,
    bool VibrationActive,
    string? LastError,
    ControllerState? LiveState);

public sealed record PuckyLiveFrame(long ReportsReceived, ControllerState? State);

public sealed class ControllerService : BackgroundService
{
    private static readonly TimeSpan OutputRetryInterval = TimeSpan.FromSeconds(5);
    public static readonly string Version = typeof(ControllerService).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        .Split('+')[0] ?? "dev";

    private readonly IControllerTransport _transport;
    private readonly VirtualOutputFactory _outputFactory;
    private readonly ProfileStore _profiles;
    private readonly SettingsStore _settings;
    private readonly HandOffMonitor _handOff;
    private readonly ILogger<ControllerService> _logger;
    private readonly MappingEngine _mapping = new();
    private readonly TrackpadHapticEngine _trackpadHaptics = new();
    private readonly IDesktopOutput _desktop = DesktopOutputFactory.Create();
    private readonly object _statusLock = new();
    private IVirtualOutput _output;
    private GamepadOutputType _outputType;
    private volatile bool _outputChangeRequested;
    private volatile bool _enabled = true;
    private bool _active;
    private string? _pausedReason;
    private ControllerState? _liveState;
    private KeySet _heldKeys;
    private long _reportsReceived;
    private string? _lastError;
    private OutputFeedback _gameRumble = new(0, 0);
    private OutputFeedback _testRumble = new(0, 0);
    private OutputFeedback _lastSentRumble = new(0, 0);
    private DateTime _testRumbleUntil = DateTime.MinValue;
    private DateTime _lastHeartbeat = DateTime.MinValue;
    private DateTime _lastRumble = DateTime.MinValue;
    private DateTime _nextOutputAttempt = DateTime.MinValue;

    public ControllerService(
        IControllerTransport transport,
        VirtualOutputFactory outputFactory,
        ProfileStore profiles,
        SettingsStore settings,
        HandOffMonitor handOff,
        ILogger<ControllerService> logger)
    {
        _transport = transport;
        _outputFactory = outputFactory;
        _profiles = profiles;
        _settings = settings;
        _handOff = handOff;
        _logger = logger;
        _outputType = EffectiveOutputType(settings.Current.OutputType);
        _output = outputFactory.Create(_outputType);
        _output.FeedbackReceived += OnFeedback;
        settings.Changed += updated =>
        {
            if (EffectiveOutputType(updated.OutputType) != _outputType)
            {
                _outputChangeRequested = true;
            }
        };
    }

    public void Enable() => _enabled = true;

    public void Disable() => _enabled = false;

    public PuckyStatus GetStatus()
    {
        lock (_statusLock)
        {
            return new PuckyStatus(
                Version,
                _enabled,
                _pausedReason,
                _transport.IsOpen,
                _transport.Device,
                _output.IsConnected,
                _output.Name,
                _outputType,
                VirtualOutputFactory.SupportedTypes(),
                _output.UnavailableReason,
                _desktop.Name,
                _profiles.Active.Name,
                _profiles.Active.Id,
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
        if (!_transport.IsOpen)
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
        if (!_transport.IsOpen)
        {
            return false;
        }

        var left = _transport.SendTrackpadHapticPulse(
            TrackpadHapticSide.Left,
            1360,
            900,
            2);
        var right = _transport.SendTrackpadHapticPulse(
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
        await _profiles.InitializeAsync(stoppingToken);
        var report = new byte[128];

        while (!stoppingToken.IsCancellationRequested)
        {
            if (_outputChangeRequested)
            {
                SwitchOutput();
            }

            var pauseReason = !_enabled
                ? "Mapping is off"
                : _handOff.ActiveProcess is { } process
                    ? $"Handed off while {process} is running"
                    : null;
            if (pauseReason is not null)
            {
                if (_active || _transport.IsOpen)
                {
                    Deactivate();
                }
                SetPaused(pauseReason);
                await Task.Delay(250, stoppingToken);
                continue;
            }
            SetPaused(null);

            if (!_transport.IsOpen)
            {
                if (_active)
                {
                    // The controller vanished mid-session: never leave the
                    // virtual pad, keys, or mouse buttons stuck down.
                    ReleaseOutputs();
                    _active = false;
                }
                if (!_transport.TryOpen())
                {
                    await Task.Delay(1000, stoppingToken);
                    continue;
                }
                _mapping.Reset();
                _trackpadHaptics.Reset();
                _lastError = null;
                if (!_transport.DisableLizardMode())
                {
                    SetError("Controller opened, but lizard mode could not be disabled.");
                }
                _lastHeartbeat = DateTime.UtcNow;
            }
            _active = true;

            if (!_output.IsConnected && DateTime.UtcNow >= _nextOutputAttempt)
            {
                // Retrying every report would recreate the ViGEm client and
                // log a stack trace hundreds of times per second.
                if (!_output.Connect())
                {
                    _nextOutputAttempt = DateTime.UtcNow + OutputRetryInterval;
                }
            }

            MaintainController();
            var count = _transport.Read(report);
            if (count <= 0)
            {
                if (_transport.IsOpen)
                {
                    _desktop.Submit(
                        _mapping.ContinueDesktopMotion(
                            DateTimeOffset.UtcNow,
                            _profiles.Active,
                            _heldKeys));
                }
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

            var profile = _profiles.Active;
            var mapped = _mapping.Map(state, profile);
            foreach (var pulse in _trackpadHaptics.Update(state, profile))
            {
                _transport.SendTrackpadHapticPulse(
                    pulse.Side,
                    pulse.OnMicroseconds,
                    pulse.OffMicroseconds,
                    pulse.RepeatCount);
            }
            _output.Submit(mapped.Gamepad);
            _desktop.Submit(mapped.Desktop);
            _heldKeys = mapped.Desktop.Keys;
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
        await _transport.DisposeAsync();
    }

    private static GamepadOutputType EffectiveOutputType(GamepadOutputType requested) =>
        VirtualOutputFactory.Supports(requested) ? requested : GamepadOutputType.Xbox360;

    private void SwitchOutput()
    {
        _outputChangeRequested = false;
        var type = EffectiveOutputType(_settings.Current.OutputType);
        if (type == _outputType)
        {
            return;
        }

        _logger.LogInformation("Switching virtual gamepad to {OutputType}", type);
        _output.FeedbackReceived -= OnFeedback;
        _output.Dispose();
        var output = _outputFactory.Create(type);
        output.FeedbackReceived += OnFeedback;
        lock (_statusLock)
        {
            _output = output;
            _outputType = type;
            _gameRumble = new OutputFeedback(0, 0);
        }
        _nextOutputAttempt = DateTime.MinValue;
    }

    private void MaintainController()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastHeartbeat).TotalMilliseconds >= 800)
        {
            _transport.DisableLizardMode();
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

    /// <summary>
    /// Releases the controller back to its built-in behavior and other apps,
    /// and removes the virtual gamepad so games fall back to the real device.
    /// </summary>
    private void Deactivate()
    {
        if (_transport.IsOpen)
        {
            _transport.SetRumble(0, 0);
            _lastSentRumble = new OutputFeedback(0, 0);
        }
        _transport.Close();
        ReleaseOutputs();
        _output.Disconnect();
        _active = false;
    }

    private void ReleaseOutputs()
    {
        try
        {
            _output.Submit(VirtualGamepadState.Neutral);
            _desktop.Submit(DesktopState.Idle);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "Could not release outputs");
        }
        _heldKeys = KeySet.Empty;
        _mapping.Reset();
        _trackpadHaptics.Reset();
        lock (_statusLock)
        {
            _liveState = null;
            _gameRumble = new OutputFeedback(0, 0);
        }
    }

    private void SetPaused(string? reason)
    {
        if (_pausedReason == reason)
        {
            return;
        }
        lock (_statusLock)
        {
            _pausedReason = reason;
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
        var sent = _transport.SetRumble(
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
        _logger.LogWarning("{Error}", error);
    }
}
