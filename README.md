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
- Experimental macOS system-wide HID gamepad output for appropriately signed
  or locally security-relaxed builds
- Native pointer/scroll output on Windows, Linux, and macOS
- A dedicated control window that opens with Pucky, featuring live input
  visualization on the supplied controller artwork and profile editing; the
  managed window closes with the Pucky process

## Platform notes

| Platform | Raw input | Virtual gamepad | Pointer | Rumble |
| --- | --- | --- | --- | --- |
| Windows 10/11 | Yes | Xbox 360 via ViGEmBus | Yes | Yes |
| Linux | Yes | Xbox-compatible via `uinput` | `uinput` mouse, clicks, and scrolling | Yes |
| macOS | Yes | Experimental HID gamepad | Yes | Direct test/output path |

Pucky's macOS backend streams 15-byte standard HID reports to a small native
helper, which publishes a Razer Serval-compatible `IOHIDUserDevice`. Isolating
the helper keeps the restricted entitlement off the CoreCLR process. The raw
button and axis layout targets Chromium's macOS standard-gamepad mapping,
including two independent analog trigger axes. A recognized Xbox identity is
intentionally not used because macOS routes it through GameController, where
arbitrary virtual-HID trigger reports are discarded. Native Steam/SDL clients
may apply a different built-in Serval profile, so this output remains
experimental outside Chromium. The backend is input-only, so game-driven
rumble is not yet available; Pucky's direct vibration test still works.

The helper marks its published HID as a GameController synthetic device. This
prevents macOS from also applying its native Serval button profile and treating
a Chromium shoulder-button usage as the configurable system Home shortcut.

Creating the device requires the restricted
`com.apple.developer.hid.virtual.device` entitlement. It is applied only to
`pucky-hid-helper`; the main Pucky process never invokes the restricted API.

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

### macOS virtual gamepad signing

For a normal-security Mac, the native helper needs an Apple-authorized HID
Virtual Device capability, a matching provisioning profile, and appropriate
app-bundle packaging. A paid account or signing certificate alone does not
authorize the entitlement. The build script can sign the components with an
authorized identity, but it does not create or embed that Apple-issued profile:

```sh
PUCKY_CODESIGN_IDENTITY="Developer ID Application: Your Name (TEAMID)" \
  ./scripts/build.sh osx-arm64
```

For local development without a developer account, build while SIP and AMFI
are still enabled:

```sh
PUCKY_CODESIGN_IDENTITY=- ./scripts/build.sh osx-arm64
```

This produces an unsigned `pucky` CoreCLR host and an ad-hoc-signed
`pucky-hid-helper` carrying the restricted entitlement. Only after the build
finishes, boot the development Mac with SIP and AMFI enforcement relaxed and
run `artifacts/osx-arm64/pucky`. Building first matters: on some Apple Silicon
systems, disabling SIP prevents the signed `dotnet` SDK itself from creating
CoreCLR.

If SIP is already disabled and `dotnet --info` fails, publish the managed part
on a normal-security Mac, Windows, or Linux machine:

```sh
dotnet publish src/Pucky.App/Pucky.App.csproj \
  --configuration Release \
  --runtime osx-arm64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  --output artifacts/osx-arm64
```

Copy the complete `artifacts/osx-arm64` directory and repository to the Mac.
Then compile and sign only the native helper without invoking the broken .NET
SDK:

```sh
PUCKY_SKIP_DOTNET_PUBLISH=1 \
PUCKY_CODESIGN_IDENTITY=- \
  ./scripts/build.sh osx-arm64
```

If a downloaded or copied development artifact produces a “could not verify”
Gatekeeper message and macOS does not offer **Open Anyway**, remove quarantine
from only the extracted Pucky artifact directory before launching it:

```sh
xattr -dr com.apple.quarantine "/absolute/path/to/artifacts/osx-arm64"
```

Do not run this command against a broad directory such as your home folder or
Downloads. Removing quarantine only clears the download-origin Gatekeeper
check; it does not grant the virtual-HID entitlement or relax AMFI/SIP.

An ad-hoc signature is not authorization. Pucky does not change the Mac's boot
security policy and does not recommend weakening AMFI or SIP on a general-use
Mac. Re-enable normal security after testing. When security is restored, this
development artifact should no longer be expected to launch or create the HID
device; rebuild a normal unsigned/no-HID artifact for ordinary Pucky use.

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
