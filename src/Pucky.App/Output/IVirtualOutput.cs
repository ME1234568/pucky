using Pucky.Core.Mapping;

namespace Pucky.App.Output;

public sealed record OutputFeedback(byte LargeMotor, byte SmallMotor);

public interface IVirtualOutput : IDisposable
{
    string Name { get; }
    bool IsConnected { get; }
    string? UnavailableReason { get; }
    event Action<OutputFeedback>? FeedbackReceived;
    bool Connect();
    void Submit(VirtualGamepadState state);
    void Disconnect();
}

public sealed class UnavailableOutput(string reason) : IVirtualOutput
{
    public string Name => "Unavailable";
    public bool IsConnected => false;
    public string? UnavailableReason => reason;
    public event Action<OutputFeedback>? FeedbackReceived
    {
        add { }
        remove { }
    }
    public bool Connect() => false;
    public void Submit(VirtualGamepadState state) { }
    public void Disconnect() { }
    public void Dispose() { }
}
