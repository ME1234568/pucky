#!/bin/sh
# Install (or remove) the system configuration Pucky needs on Linux:
#   - /etc/udev/rules.d/70-pucky.rules       controller + /dev/uinput access
#   - /etc/modules-load.d/pucky-uinput.conf  load uinput at boot
#
# Usage: ./install-linux.sh [--uninstall]
#
# Safe to run repeatedly. Works from an extracted release package or from
# packaging/linux in a source checkout.
set -eu

RULE_NAME=70-pucky.rules
RULE_DEST=/etc/udev/rules.d/$RULE_NAME
MODULES_CONF=/etc/modules-load.d/pucky-uinput.conf

script_dir=$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd)
rule_src=$script_dir/$RULE_NAME

# Print each argument on its own line.
say() {
  printf '%s\n' "$@"
}

warn() {
  printf 'warning: %s\n' "$*" >&2
}

die() {
  printf 'error: %s\n' "$*" >&2
  exit 1
}

usage() {
  say "Usage: $0 [--uninstall]" \
    "" \
    "Installs the Pucky udev rule to $RULE_DEST, loads the uinput" \
    "kernel module and configures it to load at boot ($MODULES_CONF)." \
    "" \
    "  --uninstall  remove both files again" \
    "  -h, --help   show this help"
}

is_steamos() {
  [ -r /etc/os-release ] && grep -qi '^ID=["]\{0,1\}steamos' /etc/os-release
}

sudo_hint() {
  say "" \
    "Administrator (sudo) access is required to install system files."
  if is_steamos || [ "$(id -un 2>/dev/null || true)" = deck ]; then
    say "" \
      "Steam Deck / SteamOS: the 'deck' user has no password by default, so" \
      "sudo cannot work until you set one. In Desktop Mode open Konsole and run:" \
      "" \
      "    passwd" \
      "" \
      "then run this script again and enter that password when asked."
  fi
  say ""
}

action=install
for arg in "$@"; do
  case $arg in
    --uninstall) action=uninstall ;;
    -h|--help) usage; exit 0 ;;
    *) usage >&2; exit 2 ;;
  esac
done

[ "$(uname -s)" = Linux ] || die "this script is for Linux only."

# Run privileged commands directly as root, otherwise through sudo.
if [ "$(id -u)" -eq 0 ]; then
  use_sudo=0
else
  use_sudo=1
  if ! command -v sudo >/dev/null 2>&1; then
    sudo_hint
    die "sudo was not found. Re-run this script as root."
  fi
  if ! sudo -n true 2>/dev/null; then
    if is_steamos; then
      say "If you have never set a password for this account, press Ctrl+C" \
        "and run 'passwd' first."
    fi
    say "Asking for your password to install system files..."
    if ! sudo -v; then
      sudo_hint
      die "could not get sudo access."
    fi
  fi
fi

priv() {
  if [ "$use_sudo" -eq 1 ]; then
    sudo "$@"
  else
    "$@"
  fi
}

reload_udev() {
  if command -v udevadm >/dev/null 2>&1; then
    priv udevadm control --reload-rules
    priv udevadm trigger --subsystem-match=hidraw --subsystem-match=misc
  else
    warn "udevadm not found; reboot to apply the udev rule changes."
  fi
}

install_files() {
  [ -f "$rule_src" ] || die "$RULE_NAME was not found next to this script ($script_dir)."

  if [ -f "$RULE_DEST" ] && cmp -s "$rule_src" "$RULE_DEST"; then
    say "udev rule already up to date: $RULE_DEST"
  else
    priv mkdir -p "$(dirname -- "$RULE_DEST")"
    priv install -m 0644 "$rule_src" "$RULE_DEST"
    say "Installed udev rule: $RULE_DEST"
  fi

  if [ -f "$MODULES_CONF" ] && [ "$(cat "$MODULES_CONF")" = uinput ]; then
    say "uinput boot config already present: $MODULES_CONF"
  else
    priv mkdir -p "$(dirname -- "$MODULES_CONF")"
    printf 'uinput\n' | priv tee "$MODULES_CONF" >/dev/null
    priv chmod 0644 "$MODULES_CONF"
    say "Configured uinput to load at boot: $MODULES_CONF"
  fi

  if command -v modprobe >/dev/null 2>&1; then
    priv modprobe uinput || warn "could not load the uinput module now; it will be tried again at boot."
  elif [ ! -e /dev/uinput ]; then
    warn "modprobe not found and /dev/uinput is missing; reboot to load uinput."
  fi

  reload_udev

  say "" \
    "Done. Unplug and reconnect the Puck or controller (or toggle Bluetooth)" \
    "so the new permissions apply, then start ./pucky."
}

uninstall_files() {
  if [ -e "$RULE_DEST" ]; then
    priv rm -f "$RULE_DEST"
    say "Removed $RULE_DEST"
  else
    say "Not installed: $RULE_DEST"
  fi

  if [ -e "$MODULES_CONF" ]; then
    priv rm -f "$MODULES_CONF"
    say "Removed $MODULES_CONF"
  else
    say "Not installed: $MODULES_CONF"
  fi

  # uinput is left loaded: Steam and other tools also rely on it.
  reload_udev
  say "Done. Pucky's system configuration has been removed."
}

case $action in
  install) install_files ;;
  uninstall) uninstall_files ;;
esac
