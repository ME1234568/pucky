using HidSharp;
using Pucky.Core.Input;

namespace Pucky.App.Devices;

public sealed class HidSharpControllerTransport(
    ILogger<HidSharpControllerTransport> logger) : IControllerTransport
{
    private readonly object _ioLock = new();
    private HidDevice? _hidDevice;
    private HidStream? _stream;
    private byte[]? _readBuffer;
    private int _featureReportLength = 64;

    public ControllerDeviceInfo? Device { get; private set; }
    public bool IsOpen => _stream is not null;

    public bool TryOpen()
    {
        DisposeStream();

        foreach (var productId in new[]
                 {
                     SteamControllerProtocol.WiredProductId,
                     SteamControllerProtocol.BluetoothProductId,
                     SteamControllerProtocol.PuckProductId
                 })
        {
            foreach (var candidate in DeviceList.Local.GetHidDevices(
                         SteamControllerProtocol.ValveVendorId,
                         productId))
            {
                try
                {
                    if (!candidate.TryOpen(out var stream))
                    {
                        continue;
                    }

                    stream.ReadTimeout = 100;
                    stream.WriteTimeout = 100;
                    if (!ProbeStateInterface(stream, candidate.GetMaxInputReportLength()))
                    {
                        stream.Dispose();
                        continue;
                    }
                    stream.ReadTimeout = 16;
                    _hidDevice = candidate;
                    _stream = stream;
                    _readBuffer = new byte[Math.Max(candidate.GetMaxInputReportLength(), 64)];
                    _featureReportLength = Math.Max(candidate.GetMaxFeatureReportLength(), 64);
                    Device = new ControllerDeviceInfo(
                        candidate.DevicePath,
                        SafeProductName(candidate),
                        candidate.VendorID,
                        candidate.ProductID,
                        productId switch
                        {
                            SteamControllerProtocol.PuckProductId => "Puck",
                            SteamControllerProtocol.BluetoothProductId => "Bluetooth",
                            _ => "USB"
                        });
                    logger.LogInformation(
                        "Opened {Name} ({Vid:X4}:{Pid:X4})",
                        Device.Name,
                        Device.VendorId,
                        Device.ProductId);
                    return true;
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Could not open HID candidate {Path}", candidate.DevicePath);
                }
            }
        }

        return false;
    }

    public int Read(Span<byte> buffer)
    {
        var stream = _stream;
        var readBuffer = _readBuffer;
        if (stream is null || readBuffer is null)
        {
            return 0;
        }

        try
        {
            var count = stream.Read(readBuffer, 0, readBuffer.Length);
            if (SteamControllerProtocol.TryParseBatteryStatus(
                    readBuffer.AsSpan(0, count),
                    out var batteryPercent,
                    out var charging) &&
                Device is { } device &&
                (device.BatteryPercent != batteryPercent || device.Charging != charging))
            {
                Device = device with
                {
                    BatteryPercent = batteryPercent,
                    Charging = charging
                };
            }

            var copied = Math.Min(count, buffer.Length);
            readBuffer.AsSpan(0, copied).CopyTo(buffer);
            return copied;
        }
        catch (TimeoutException)
        {
            return 0;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            logger.LogWarning("Steam Controller disconnected: {Message}", ex.Message);
            DisposeStream();
            return 0;
        }
    }

    public void Close()
    {
        if (_stream is not null)
        {
            RestoreLizardMode();
        }
        DisposeStream();
    }

    public bool DisableLizardMode()
    {
        var cleared = SendFeature(SteamControllerProtocol.ClearDigitalMappings, []);
        var configured = SendFeature(
            SteamControllerProtocol.SetSettings,
            SteamControllerProtocol.BuildDisableLizardSettingsPayload());
        return cleared && configured;
    }

    public bool RestoreLizardMode()
    {
        var restored = SendFeature(SteamControllerProtocol.SetDefaultMappings, []);
        if (!restored)
        {
            restored = SendFeature(SteamControllerProtocol.LoadDefaultSettings, []);
        }

        return restored;
    }

    public bool SetRumble(ushort lowFrequency, ushort highFrequency)
    {
        var stream = _stream;
        if (stream is null)
        {
            return false;
        }

        var report = SteamControllerProtocol.BuildHapticRumbleReport(
            lowFrequency,
            highFrequency);

        try
        {
            lock (_ioLock)
            {
                stream.Write(report);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or ObjectDisposedException)
        {
            logger.LogDebug(ex, "Rumble write failed");
            return false;
        }
    }

    public bool SendTrackpadHapticPulse(
        TrackpadHapticSide side,
        ushort onMicroseconds,
        ushort offMicroseconds,
        ushort repeatCount)
    {
        var stream = _stream;
        if (stream is null)
        {
            return false;
        }

        var report = SteamControllerProtocol.BuildHapticPulseReport(
            side,
            onMicroseconds,
            offMicroseconds,
            repeatCount);
        try
        {
            lock (_ioLock)
            {
                stream.Write(report);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or ObjectDisposedException)
        {
            logger.LogDebug(ex, "Trackpad haptic pulse write failed");
            return false;
        }
    }

    public ValueTask DisposeAsync()
    {
        Close();
        return ValueTask.CompletedTask;
    }

    private bool SendFeature(byte command, ReadOnlySpan<byte> payload)
    {
        var stream = _stream;
        if (stream is null)
        {
            return false;
        }

        try
        {
            var report = SteamControllerProtocol.BuildFeatureCommand(
                command,
                payload,
                _featureReportLength);
            lock (_ioLock)
            {
                stream.SetFeature(report);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or ObjectDisposedException)
        {
            logger.LogDebug(ex, "Feature command 0x{Command:X2} failed", command);
            return false;
        }
    }

    private static bool ProbeStateInterface(HidStream stream, int reportLength)
    {
        try
        {
            var report = new byte[Math.Max(reportLength, 64)];
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var count = stream.Read(report, 0, report.Length);
                if (count > 0 && report[0] is
                    SteamControllerProtocol.StateReport or
                    SteamControllerProtocol.LegacyStateReport or
                    SteamControllerProtocol.TimestampStateReport)
                {
                    return true;
                }
            }
        }
        catch (TimeoutException)
        {
        }
        return false;
    }

    private static string SafeProductName(HidDevice device)
    {
        try
        {
            return device.GetProductName();
        }
        catch
        {
            return "Steam Controller (2026)";
        }
    }

    private void DisposeStream()
    {
        lock (_ioLock)
        {
            _stream?.Dispose();
            _stream = null;
            _readBuffer = null;
            _hidDevice = null;
            Device = null;
        }
    }
}
