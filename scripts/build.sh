#!/usr/bin/env sh
set -eu

runtime="${1:-linux-x64}"
root="$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)"
output="$root/artifacts/$runtime"

if [ "${PUCKY_SKIP_DOTNET_PUBLISH:-0}" = "1" ]; then
  if [ ! -f "$output/pucky" ]; then
    printf 'PUCKY_SKIP_DOTNET_PUBLISH=1, but %s does not exist.\n' "$output/pucky" >&2
    printf '%s\n' 'Publish the managed osx artifact on another machine and copy the complete output directory here first.' >&2
    exit 2
  fi
  printf 'Using the existing managed publish at %s\n' "$output"
else
  dotnet publish "$root/src/Pucky.App/Pucky.App.csproj" \
    --configuration Release \
    --runtime "$runtime" \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    --output "$output"
fi

case "$runtime" in
  osx-*)
    case "$runtime" in
      osx-arm64) helper_arch="arm64" ;;
      osx-x64) helper_arch="x86_64" ;;
      *)
        printf 'Unsupported macOS runtime: %s\n' "$runtime" >&2
        exit 2
        ;;
    esac

    if ! command -v xcrun >/dev/null 2>&1; then
      printf '%s\n' 'xcrun was not found. Install the Xcode Command Line Tools to build the macOS HID helper.' >&2
      exit 2
    fi

    xcrun --sdk macosx clang \
      -arch "$helper_arch" \
      -std=c11 \
      -Wall \
      -Wextra \
      -framework CoreFoundation \
      -framework IOKit \
      "$root/packaging/macos/pucky-hid-helper.c" \
      -o "$output/pucky-hid-helper"

    if [ -n "${PUCKY_CODESIGN_IDENTITY:-}" ]; then
      if [ "$PUCKY_CODESIGN_IDENTITY" = "-" ]; then
        # A signed CoreCLR apphost can fail to initialize when SIP is disabled.
        # With AMFI relaxed, leave Pucky unsigned and isolate the restricted
        # entitlement in the native helper process.
        codesign --remove-signature "$output/pucky" 2>/dev/null || true
        codesign --force \
          --sign - \
          --entitlements "$root/packaging/macos/pucky-hid-helper.entitlements" \
          "$output/pucky-hid-helper"
        codesign --verify --strict --verbose=2 "$output/pucky-hid-helper"
        printf '%s\n' \
          'Built the AMFI/SIP-relaxed development configuration:' \
          '  pucky: unsigned CoreCLR host' \
          '  pucky-hid-helper: ad-hoc signed with the restricted HID entitlement'
      else
        codesign --force \
          --options runtime \
          --timestamp \
          --sign "$PUCKY_CODESIGN_IDENTITY" \
          --entitlements "$root/packaging/macos/pucky.entitlements" \
          "$output/pucky"
        codesign --force \
          --options runtime \
          --timestamp \
          --sign "$PUCKY_CODESIGN_IDENTITY" \
          --entitlements "$root/packaging/macos/pucky-hid-helper.entitlements" \
          "$output/pucky-hid-helper"
        codesign --verify --strict --verbose=2 "$output/pucky"
        codesign --verify --strict --verbose=2 "$output/pucky-hid-helper"
      fi
    else
      printf '%s\n' \
        'macOS gamepad output is not enabled in this unsigned build.' \
        'Set PUCKY_CODESIGN_IDENTITY=- while SIP is enabled, then run the artifact only after AMFI/SIP are relaxed.'
    fi
    ;;
esac

printf 'Pucky published to %s\n' "$output"
