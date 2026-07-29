using Pucky.Core.Mapping;

namespace Pucky.App.Output;

public interface IDesktopOutput : IDisposable
{
    string Name { get; }
    bool IsAvailable { get; }
    void Submit(DesktopState state);
}

public sealed class NoDesktopOutput(string reason) : IDesktopOutput
{
    public string Name => reason;
    public bool IsAvailable => false;
    public void Submit(DesktopState state) { }
    public void Dispose() { }
}
