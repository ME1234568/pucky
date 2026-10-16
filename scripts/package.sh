#!/usr/bin/env sh
# Build Pucky for a Linux or macOS runtime and assemble a release archive:
#   artifacts/dist/pucky-<version>-<rid>.tar.gz   (linux-*)
#   artifacts/dist/pucky-<version>-<rid>.zip      (osx-*)
#
# Usage: scripts/package.sh <rid> [version]
#   rid      linux-x64 | linux-arm64 | osx-x64 | osx-arm64
#   version  defaults to <Version> in Directory.Build.props
#
# Environment variables understood by scripts/build.sh (for example
# PUCKY_CODESIGN_IDENTITY) are passed through. Use scripts/package.ps1 for
# Windows runtimes.
set -eu

die() {
  printf 'package.sh: %s\n' "$*" >&2
  exit 2
}

runtime="${1:-}"
version="${2:-}"
root="$(CDPATH='' cd -- "$(dirname -- "$0")/.." && pwd)"

case "$runtime" in
  linux-x64|linux-arm64) kind=linux ;;
  osx-x64|osx-arm64) kind=osx ;;
  win-*) die "use scripts/package.ps1 for Windows runtimes." ;;
  *) die "usage: scripts/package.sh <linux-x64|linux-arm64|osx-x64|osx-arm64> [version]" ;;
esac

if [ -z "$version" ]; then
  version="$(sed -n 's:.*<Version>[[:space:]]*\([^<[:space:]]*\)[[:space:]]*</Version>.*:\1:p' \
    "$root/Directory.Build.props" | head -n 1)"
  [ -n "$version" ] || die "could not read <Version> from Directory.Build.props."
fi
case "$version" in
  v*) version="${version#v}" ;;
esac
case "$version" in
  ''|*[!0-9A-Za-z.+-]*) die "invalid version: $version" ;;
esac

sh "$root/scripts/build.sh" "$runtime"

publish="$root/artifacts/$runtime"
name="pucky-$version-$runtime"
stage_root="$root/artifacts/stage"
stage="$stage_root/$name"
dist="$root/artifacts/dist"

rm -rf "$stage"
mkdir -p "$stage" "$dist"

cp -R "$publish/." "$stage/"
# Debug symbols are not useful to end users and only grow the download.
find "$stage" -name '*.pdb' -type f -exec rm -f {} +

# Copy a text file, dropping any CR characters (a Windows checkout with
# core.autocrlf would otherwise break shell scripts and udev rules).
copy_text() {
  tr -d '\r' < "$1" > "$2"
}

copy_text "$root/LICENSE" "$stage/LICENSE.txt"

case "$kind" in
  linux)
    copy_text "$root/packaging/linux/INSTALL.txt" "$stage/INSTALL.txt"
    copy_text "$root/packaging/linux/70-pucky.rules" "$stage/70-pucky.rules"
    copy_text "$root/packaging/linux/install-linux.sh" "$stage/install-linux.sh"
    chmod 0644 "$stage/70-pucky.rules"
    chmod 0755 "$stage/install-linux.sh" "$stage/pucky"
    ;;
  osx)
    copy_text "$root/packaging/macos/INSTALL.txt" "$stage/INSTALL.txt"
    chmod 0755 "$stage/pucky"
    if [ -f "$stage/pucky-hid-helper" ]; then
      chmod 0755 "$stage/pucky-hid-helper"
    fi
    ;;
esac

case "$kind" in
  linux)
    archive="$dist/$name.tar.gz"
    rm -f "$archive"
    if tar --version 2>/dev/null | grep -q 'GNU tar'; then
      tar -C "$stage_root" --owner=0 --group=0 --numeric-owner -czf "$archive" "$name"
    else
      tar -C "$stage_root" -czf "$archive" "$name"
    fi
    ;;
  osx)
    archive="$dist/$name.zip"
    rm -f "$archive"
    if command -v ditto >/dev/null 2>&1; then
      # ditto keeps permissions and code signatures intact.
      ditto -c -k --keepParent "$stage" "$archive"
    elif command -v zip >/dev/null 2>&1; then
      (cd "$stage_root" && zip -qry "$archive" "$name")
    else
      die "neither ditto nor zip is available to create $archive."
    fi
    ;;
esac

rm -rf "$stage"
printf 'Packaged %s\n' "$archive"
