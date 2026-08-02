using System.Runtime.InteropServices;
using Pucky.Core.Mapping;

namespace Pucky.App.Output;

public sealed class MacHidGamepadOutput(
    ILogger<MacHidGamepadOutput> logger) : IVirtualOutput
{
    private const string VirtualHidEntitlement = "com.apple.developer.hid.virtual.device";
    private const int Success = 0;
    private IntPtr _device;
    private bool _submitErrorLogged;
    private DateTime _nextConnectAttempt;
    private bool? _hasVirtualHidEntitlement;

    public string Name => "Razer Serval-compatible virtual HID gamepad";
    public bool IsConnected => _device != IntPtr.Zero;
    public string? UnavailableReason { get; private set; }
    public event Action<OutputFeedback>? FeedbackReceived
    {
        add { }
        remove { }
    }

    public bool Connect()
    {
        if (_device != IntPtr.Zero)
        {
            return true;
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

        try
        {
            // Calling IOHIDUserDeviceCreate without the restricted entitlement
            // can cause AMFI to terminate the process. Check the signed binary
            // first so ordinary unsigned builds fail safely.
            _hasVirtualHidEntitlement ??=
                MacNative.HasBooleanEntitlement(VirtualHidEntitlement);
            if (!_hasVirtualHidEntitlement.Value)
            {
                UnavailableReason =
                    "Pucky is not signed with the macOS virtual-HID entitlement. " +
                    "Use an Apple-authorized signing identity, or an ad-hoc signed " +
                    "development build on a Mac whose security policy permits it.";
                return false;
            }

            using var properties = new MacNative.CfDictionary();
            properties.SetNumber("VendorID", 0x1532);
            properties.SetNumber("ProductID", 0x0900);
            // Match the macOS SDL mapping GUID for the USB Razer Serval.
            properties.SetNumber("VersionNumber", 0x0200);
            properties.SetNumber("PrimaryUsagePage", 0x01);
            properties.SetNumber("PrimaryUsage", 0x05);
            properties.SetString("Manufacturer", "Razer");
            properties.SetString("Product", "Razer Serval");
            properties.SetString("SerialNumber", "PUCKY-VIRTUAL-1");
            properties.SetString("Transport", "USB");
            properties.SetData("ReportDescriptor", MacHidGamepadReport.ReportDescriptor);

            _device = MacNative.IOHIDUserDeviceCreate(IntPtr.Zero, properties.Handle);
            if (_device == IntPtr.Zero)
            {
                UnavailableReason =
                    "macOS refused to create the virtual HID gamepad. Verify the " +
                    "signature, entitlement authorization, and local security policy.";
                return false;
            }

            _submitErrorLogged = false;
            UnavailableReason = null;
            logger.LogInformation("Created the macOS virtual HID gamepad");
            return true;
        }
        catch (Exception ex) when (
            ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            UnavailableReason = $"The macOS virtual HID backend could not start: {ex.Message}";
            logger.LogWarning(ex, "Could not create the macOS virtual HID gamepad");
            Disconnect();
            return false;
        }
    }

    public void Submit(VirtualGamepadState state)
    {
        var device = _device;
        if (device == IntPtr.Zero)
        {
            return;
        }

        var report = MacHidGamepadReport.Encode(state);
        var result = MacNative.IOHIDUserDeviceHandleReport(device, report, report.Length);
        if (result != Success && !_submitErrorLogged)
        {
            _submitErrorLogged = true;
            logger.LogWarning(
                "macOS rejected a virtual gamepad report with IOReturn 0x{Result:X8}",
                result);
        }
    }

    public void Disconnect()
    {
        var device = _device;
        _device = IntPtr.Zero;
        if (device != IntPtr.Zero)
        {
            MacNative.CFRelease(device);
        }
    }

    public void Dispose() => Disconnect();

    private static class MacNative
    {
        private const string CoreFoundation =
            "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private const string IOKit =
            "/System/Library/Frameworks/IOKit.framework/IOKit";
        private const string Security =
            "/System/Library/Frameworks/Security.framework/Security";
        private const uint Utf8Encoding = 0x08000100;
        private const int SignedInt32Number = 3;

        [DllImport(IOKit)]
        internal static extern IntPtr IOHIDUserDeviceCreate(
            IntPtr allocator,
            IntPtr properties);

        [DllImport(IOKit)]
        internal static extern int IOHIDUserDeviceHandleReport(
            IntPtr device,
            byte[] report,
            nint reportLength);

        [DllImport(CoreFoundation)]
        internal static extern void CFRelease(IntPtr value);

        [DllImport(CoreFoundation)]
        private static extern IntPtr CFDictionaryCreateMutable(
            IntPtr allocator,
            nint capacity,
            IntPtr keyCallbacks,
            IntPtr valueCallbacks);

        [DllImport(CoreFoundation)]
        private static extern void CFDictionarySetValue(
            IntPtr dictionary,
            IntPtr key,
            IntPtr value);

        [DllImport(CoreFoundation)]
        private static extern IntPtr CFStringCreateWithCString(
            IntPtr allocator,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string value,
            uint encoding);

        [DllImport(CoreFoundation)]
        private static extern IntPtr CFNumberCreate(
            IntPtr allocator,
            int numberType,
            ref int value);

        [DllImport(CoreFoundation)]
        private static extern IntPtr CFDataCreate(
            IntPtr allocator,
            byte[] bytes,
            nint length);

        [DllImport(CoreFoundation)]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool CFBooleanGetValue(IntPtr value);

        [DllImport(Security)]
        private static extern IntPtr SecTaskCreateFromSelf(IntPtr allocator);

        [DllImport(Security)]
        private static extern IntPtr SecTaskCopyValueForEntitlement(
            IntPtr task,
            IntPtr entitlement,
            IntPtr error);

        internal static bool HasBooleanEntitlement(string name)
        {
            var task = SecTaskCreateFromSelf(IntPtr.Zero);
            var key = CFStringCreateWithCString(IntPtr.Zero, name, Utf8Encoding);
            if (task == IntPtr.Zero || key == IntPtr.Zero)
            {
                if (task != IntPtr.Zero) CFRelease(task);
                if (key != IntPtr.Zero) CFRelease(key);
                return false;
            }

            try
            {
                var value = SecTaskCopyValueForEntitlement(task, key, IntPtr.Zero);
                if (value == IntPtr.Zero)
                {
                    return false;
                }

                try
                {
                    return CFBooleanGetValue(value);
                }
                finally
                {
                    CFRelease(value);
                }
            }
            finally
            {
                CFRelease(key);
                CFRelease(task);
            }
        }

        internal sealed class CfDictionary : IDisposable
        {
            public CfDictionary()
            {
                var framework = NativeLibrary.Load(CoreFoundation);
                var keyCallbacks = NativeLibrary.GetExport(
                    framework,
                    "kCFTypeDictionaryKeyCallBacks");
                var valueCallbacks = NativeLibrary.GetExport(
                    framework,
                    "kCFTypeDictionaryValueCallBacks");
                Handle = CFDictionaryCreateMutable(
                    IntPtr.Zero,
                    0,
                    keyCallbacks,
                    valueCallbacks);
                if (Handle == IntPtr.Zero)
                {
                    throw new InvalidOperationException("Could not allocate a CoreFoundation dictionary.");
                }
            }

            public IntPtr Handle { get; private set; }

            public void SetNumber(string key, int value)
            {
                var number = CFNumberCreate(IntPtr.Zero, SignedInt32Number, ref value);
                Set(key, number);
            }

            public void SetString(string key, string value)
            {
                var text = CFStringCreateWithCString(IntPtr.Zero, value, Utf8Encoding);
                Set(key, text);
            }

            public void SetData(string key, ReadOnlySpan<byte> value)
            {
                var bytes = value.ToArray();
                var data = CFDataCreate(IntPtr.Zero, bytes, bytes.Length);
                Set(key, data);
            }

            public void Dispose()
            {
                var handle = Handle;
                Handle = IntPtr.Zero;
                if (handle != IntPtr.Zero)
                {
                    CFRelease(handle);
                }
            }

            private void Set(string key, IntPtr value)
            {
                var keyValue = CFStringCreateWithCString(IntPtr.Zero, key, Utf8Encoding);
                if (keyValue == IntPtr.Zero || value == IntPtr.Zero)
                {
                    if (keyValue != IntPtr.Zero) CFRelease(keyValue);
                    if (value != IntPtr.Zero) CFRelease(value);
                    throw new InvalidOperationException($"Could not create the CoreFoundation value for '{key}'.");
                }

                CFDictionarySetValue(Handle, keyValue, value);
                CFRelease(keyValue);
                CFRelease(value);
            }
        }
    }
}
