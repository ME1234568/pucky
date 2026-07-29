using System.Runtime.InteropServices;
using Pucky.Core.Mapping;

namespace Pucky.App.Output;

public sealed class MacDesktopOutput : IDesktopOutput
{
    private const uint HidEventTap = 0;
    private bool _leftDown;

    public string Name => "macOS Quartz events";
    public bool IsAvailable => true;

    public void Submit(DesktopState state)
    {
        if (Math.Abs(state.MouseX) > 0.01f || Math.Abs(state.MouseY) > 0.01f)
        {
            var current = CGEventCreate(IntPtr.Zero);
            var position = CGEventGetLocation(current);
            CFRelease(current);
            var type = state.LeftClick ? 6u : 5u;
            var move = CGEventCreateMouseEvent(
                IntPtr.Zero,
                type,
                new CGPoint(position.X + state.MouseX, position.Y + state.MouseY),
                state.LeftClick ? 0u : 0u);
            CGEventPost(HidEventTap, move);
            CFRelease(move);
        }

        if (state.LeftClick != _leftDown)
        {
            var current = CGEventCreate(IntPtr.Zero);
            var position = CGEventGetLocation(current);
            CFRelease(current);
            var click = CGEventCreateMouseEvent(
                IntPtr.Zero,
                state.LeftClick ? 1u : 2u,
                position,
                0);
            CGEventPost(HidEventTap, click);
            CFRelease(click);
            _leftDown = state.LeftClick;
        }

        if (Math.Abs(state.ScrollY) > 0.01f)
        {
            var scroll = CGEventCreateScrollWheelEvent(
                IntPtr.Zero,
                0,
                1,
                (int)Math.Round(state.ScrollY));
            CGEventPost(HidEventTap, scroll);
            CFRelease(scroll);
        }
    }

    public void Dispose() { }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CGPoint(double X, double Y);

    [DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
    private static extern IntPtr CGEventCreate(IntPtr source);

    [DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
    private static extern CGPoint CGEventGetLocation(IntPtr eventRef);

    [DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
    private static extern IntPtr CGEventCreateMouseEvent(
        IntPtr source,
        uint mouseType,
        CGPoint position,
        uint button);

    [DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
    private static extern IntPtr CGEventCreateScrollWheelEvent(
        IntPtr source,
        uint units,
        uint wheelCount,
        int wheel1);

    [DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
    private static extern void CGEventPost(uint tap, IntPtr eventRef);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRelease(IntPtr value);
}
