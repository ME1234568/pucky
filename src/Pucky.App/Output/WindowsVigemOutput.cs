using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using Pucky.Core.Input;
using Pucky.Core.Mapping;

namespace Pucky.App.Output;

public sealed class WindowsVigemOutput(
    ILogger<WindowsVigemOutput> logger) : IVirtualOutput
{
    private ViGEmClient? _client;
    private IXbox360Controller? _controller;

    public string Name => "Xbox 360 (ViGEmBus)";
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
            _controller = _client.CreateXbox360Controller();
            _controller.AutoSubmitReport = false;
            _controller.FeedbackReceived += OnFeedback;
            _controller.Connect();
            UnavailableReason = null;
            return true;
        }
        catch (Exception ex)
        {
            UnavailableReason =
                "ViGEmBus is not installed or could not be opened. Install ViGEmBus, then restart Pucky.";
            logger.LogWarning(ex, "Could not create the virtual Xbox controller");
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

        SetButton(controller, Xbox360Button.A, state.Buttons, VirtualButton.A);
        SetButton(controller, Xbox360Button.B, state.Buttons, VirtualButton.B);
        SetButton(controller, Xbox360Button.X, state.Buttons, VirtualButton.X);
        SetButton(controller, Xbox360Button.Y, state.Buttons, VirtualButton.Y);
        SetButton(controller, Xbox360Button.LeftShoulder, state.Buttons, VirtualButton.LeftBumper);
        SetButton(controller, Xbox360Button.RightShoulder, state.Buttons, VirtualButton.RightBumper);
        SetButton(controller, Xbox360Button.Back, state.Buttons, VirtualButton.Back);
        SetButton(controller, Xbox360Button.Start, state.Buttons, VirtualButton.Start);
        SetButton(controller, Xbox360Button.Guide, state.Buttons, VirtualButton.Guide);
        SetButton(controller, Xbox360Button.LeftThumb, state.Buttons, VirtualButton.LeftStick);
        SetButton(controller, Xbox360Button.RightThumb, state.Buttons, VirtualButton.RightStick);
        SetButton(controller, Xbox360Button.Up, state.Buttons, VirtualButton.DPadUp);
        SetButton(controller, Xbox360Button.Down, state.Buttons, VirtualButton.DPadDown);
        SetButton(controller, Xbox360Button.Left, state.Buttons, VirtualButton.DPadLeft);
        SetButton(controller, Xbox360Button.Right, state.Buttons, VirtualButton.DPadRight);

        controller.SetAxisValue(Xbox360Axis.LeftThumbX, ToShort(state.LeftStick.X));
        controller.SetAxisValue(Xbox360Axis.LeftThumbY, ToShort(state.LeftStick.Y));
        controller.SetAxisValue(Xbox360Axis.RightThumbX, ToShort(state.RightStick.X));
        controller.SetAxisValue(Xbox360Axis.RightThumbY, ToShort(state.RightStick.Y));
        controller.SetSliderValue(Xbox360Slider.LeftTrigger, ToByte(state.LeftTrigger));
        controller.SetSliderValue(Xbox360Slider.RightTrigger, ToByte(state.RightTrigger));
        controller.SubmitReport();
    }

    public void Disconnect()
    {
        if (_controller is not null)
        {
            try
            {
                _controller.FeedbackReceived -= OnFeedback;
                _controller.Disconnect();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Error while disconnecting ViGEm target");
            }
            _controller = null;
        }

        _client?.Dispose();
        _client = null;
    }

    public void Dispose() => Disconnect();

    private void OnFeedback(object? sender, Xbox360FeedbackReceivedEventArgs e) =>
        FeedbackReceived?.Invoke(new OutputFeedback(e.LargeMotor, e.SmallMotor));

    private static void SetButton(
        IXbox360Controller controller,
        Xbox360Button target,
        VirtualButton actual,
        VirtualButton expected) =>
        controller.SetButtonState(target, actual.HasFlag(expected));

    private static short ToShort(float value)
    {
        value = Math.Clamp(value, -1, 1);
        return value < 0
            ? (short)Math.Round(value * 32768f)
            : (short)Math.Round(value * 32767f);
    }

    private static byte ToByte(float value) =>
        (byte)Math.Round(Math.Clamp(value, 0, 1) * 255f);
}
