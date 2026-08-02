# Pucky

Pucky is an open-source, cross-platform input mapper for Valve's 2026 Steam
Controller. It reads the controller directly over raw HID, so Steam does not
need to be installed or running.

The app is deliberately local: the mapper and its control panel run on your
machine, profiles are ordinary JSON files, and there is no account or
telemetry.

## What works

- USB-C (`28DE:1302`), Bluetooth (`28DE:1303`), and Puck (`28DE:1304`)
  discovery
- All face, menu, Quick Access, stick, bumper, trigger-click, back, pad-click,
  and capacitive-touch buttons
- Analog sticks, triggers, both pressure-sensitive trackpads, accelerometer,
  and gyroscope
- Radial deadzones, arbitrary button mappings, hold layers, trackpad mouse,
  trackpad joystick/D-pad/scroll modes, and gyro mouse/right-stick modes
- Lizard-mode suppression while mapping and automatic restoration on shutdown
- Lizard-style trackpad ticks and click pulses with per-pad enable and intensity
  controls
- Windows system-wide Xbox 360 output, game-driven vibration, and an in-app
  vibration test
- Linux system-wide Xbox-compatible `uinput` output
- Native pointer/scroll output on Windows, Linux, and macOS
- A dedicated control window that opens with Pucky, featuring live input
  visualization on the supplied controller artwork and profile editing; the
  managed window closes with the Pucky process

## Platform notes

| Platform | Raw input | Virtual gamepad | Pointer | Rumble |
| --- | --- | --- | --- | --- |
| Windows 10/11 | Yes | Xbox 360 via ViGEmBus | Yes | Yes |
| Linux | Yes | Xbox-compatible via `uinput` | `uinput` mouse, clicks, and scrolling | Yes |
| macOS | Yes | Not system-wide | Yes | Direct test/output path |

macOS does not currently provide a generally available system-wide virtual
gamepad API. Apple documents that virtual game controllers are not reliably
available to ordinary apps; a distributable implementation needs Apple's
virtual-HID entitlement or a DriverKit extension. Pucky therefore exposes raw
input, profiles, diagnostics, gyro, touchpads, and pointer output on macOS, but
does not pretend to create a gamepad that games cannot see.

Firmware updates and Puck pairing remain firmware-management operations. Do
those once with Steam or Valve's supported tooling; normal Pucky use does not
need Steam.

## Run from source

Requirements: the .NET 8 SDK or newer.

```powershell
dotnet restore Pucky.sln
dotnet run --project src/Pucky.App
```

Pucky opens its control panel in a dedicated app-style browser window. If no
compatible browser is available, it falls back to the default browser at
`http://127.0.0.1:27182`.

### Windows prerequisite

Install the
[ViGEmBus driver](https://github.com/nefarius/ViGEmBus/releases), reboot if its
installer asks, and run Pucky. The physical controller is opened directly and
games see a standard Xbox 360 controller.

### Linux permissions

Install the supplied rule and reload udev:

```sh
sudo install -m 0644 packaging/linux/70-pucky.rules /etc/udev/rules.d/70-pucky.rules
sudo udevadm control --reload-rules
sudo udevadm trigger
```

Unplug/replug the Puck or controller after installing the rule.

## Build a standalone executable

Windows:

```powershell
.\scripts\build.ps1 -Runtime win-x64
```

Linux/macOS:

```sh
./scripts/build.sh linux-x64
# Valid alternatives include linux-arm64, osx-x64, and osx-arm64.
```

Outputs go to `artifacts/<runtime>/`. Builds are self-contained and do not
require users to install .NET.

## Profiles

Profiles live under `data/profiles` beside the running application. The
control panel edits the common settings. Advanced button mappings and action
layers can also be edited directly; changes are loaded when a profile is
activated.

Example layer:

```json
{
  "name": "Back-button shift",
  "holdButton": "L4",
  "buttonMappings": {
    "A": "Y",
    "R5": "Guide"
  }
}
```

## Design

`Pucky.Core` contains the protocol parser and pure mapping engine.
`Pucky.App` contains cross-platform HID access, operating-system output
backends, the background mapper, profile persistence, and the local UI.
`Pucky.Tests` is a dependency-free protocol/mapping test runner.

The Triton report definitions and haptic packet layout are independently
implemented from the permissively licensed
[SDL Steam Controller driver](https://github.com/libsdl-org/SDL/blob/main/src/joystick/hidapi/SDL_hidapi_steam_triton.c)
and its
[controller structures](https://github.com/libsdl-org/SDL/blob/main/src/joystick/hidapi/steam/controller_structs.h).

## Security

The server binds to `127.0.0.1` only. Do not change `PUCKY_URL` to a public
interface unless you also add authentication. Profiles are validated and
written atomically.

## License

MIT
