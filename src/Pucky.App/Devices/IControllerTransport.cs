using Pucky.Core.Input;

namespace Pucky.App.Devices;

public interface IControllerTransport : IAsyncDisposable
{
    ControllerDeviceInfo? Device { get; }
    bool IsOpen { get; }
    bool TryOpen();
    int Read(Span<byte> buffer);
    bool DisableLizardMode();
    bool RestoreLizardMode();
    bool SetRumble(ushort lowFrequency, ushort highFrequency);
    bool SendTrackpadHapticPulse(
        TrackpadHapticSide side,
        ushort onMicroseconds,
        ushort offMicroseconds,
        ushort repeatCount);
}
