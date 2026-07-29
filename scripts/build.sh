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

printf 'Pucky published to %s\n' "$output"
