#!/usr/bin/env sh
set -eu

runtime="${1:-linux-x64}"
root="$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)"
output="$root/artifacts/$runtime"

dotnet publish "$root/src/Pucky.App/Pucky.App.csproj" \
  --configuration Release \
  --runtime "$runtime" \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  --output "$output"

case "$runtime" in
  osx-*)
    if [ -n "${PUCKY_CODESIGN_IDENTITY:-}" ]; then
      if [ "$PUCKY_CODESIGN_IDENTITY" = "-" ]; then
        codesign --force \
          --sign - \
          --entitlements "$root/packaging/macos/pucky.entitlements" \
          "$output/pucky"
      else
        codesign --force \
          --options runtime \
          --timestamp \
          --sign "$PUCKY_CODESIGN_IDENTITY" \
          --entitlements "$root/packaging/macos/pucky.entitlements" \
          "$output/pucky"
      fi
      codesign --verify --strict --verbose=2 "$output/pucky"
    else
      printf '%s\n' \
        'macOS gamepad output is not enabled in this unsigned build.' \
        'Set PUCKY_CODESIGN_IDENTITY to an authorized identity, or to - only for an AMFI-relaxed development Mac.'
    fi
    ;;
esac

printf 'Pucky published to %s\n' "$output"
