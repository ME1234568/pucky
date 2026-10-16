const $ = selector => document.querySelector(selector);
const $$ = selector => [...document.querySelectorAll(selector)];
const serviceOrigin = location.protocol === "file:" ? "http://127.0.0.1:27182" : "";
const managedWindow = new URLSearchParams(location.search).get("puckyWindow") === "1";

const state = {
  enabled: true,
  profile: null,
  savedSnapshot: "",
  profiles: [],
  profilesLoaded: false,
  activeProfileId: null,
  editLayer: -1,
  settings: null,
  serviceOnline: false,
  lastServiceSeen: Date.now(),
  lastReportCount: 0,
  lastReportAt: performance.now(),
  latestReportCount: 0,
  capture: null,
  target: {
    leftStick: { x: 0, y: 0 },
    rightStick: { x: 0, y: 0 },
    leftPad: { x: 0, y: 0 },
    rightPad: { x: 0, y: 0 }
  },
  display: {
    leftStick: { x: 0, y: 0 },
    rightStick: { x: 0, y: 0 },
    leftPad: { x: 0, y: 0 },
    rightPad: { x: 0, y: 0 }
  }
};

const controls = {
  name: $("#profile-name"),
  leftDeadzone: $("#left-deadzone"),
  rightDeadzone: $("#right-deadzone"),
  leftPadMode: $("#left-pad-mode"),
  leftPadSensitivity: $("#left-pad-sensitivity"),
  leftPadDeadzone: $("#left-pad-deadzone"),
  leftPadAcceleration: $("#left-pad-acceleration"),
  leftPadClick: $("#left-pad-click"),
  rightPadMode: $("#right-pad-mode"),
  rightPadSensitivity: $("#right-pad-sensitivity"),
  rightPadDeadzone: $("#right-pad-deadzone"),
  rightPadAcceleration: $("#right-pad-acceleration"),
  rightPadClick: $("#right-pad-click"),
  leftPadHaptics: $("#left-pad-haptics"),
  rightPadHaptics: $("#right-pad-haptics"),
  leftHapticIntensity: $("#left-haptic-intensity"),
  rightHapticIntensity: $("#right-haptic-intensity"),
  gyroMode: $("#gyro-mode"),
  gyroEnable: $("#gyro-enable"),
  gyroSensitivity: $("#gyro-sensitivity"),
  gyroDeadzone: $("#gyro-deadzone"),
  gyroAxis: $("#gyro-axis"),
  gyroInvertX: $("#gyro-invert-x"),
  gyroInvertY: $("#gyro-invert-y")
};

// Physical inputs, grouped as they appear on the controller.
const sourceGroups = [
  ["Face buttons", [["A", "A"], ["B", "B"], ["X", "X"], ["Y", "Y"]]],
  ["D-pad", [["DPadUp", "D-pad up"], ["DPadDown", "D-pad down"], ["DPadLeft", "D-pad left"], ["DPadRight", "D-pad right"]]],
  ["Shoulders", [["LeftBumper", "Left bumper"], ["RightBumper", "Right bumper"], ["LeftTriggerFull", "Left trigger full pull"], ["RightTriggerFull", "Right trigger full pull"]]],
  ["Sticks", [["LeftStick", "Left stick click"], ["RightStick", "Right stick click"]]],
  ["Center", [["View", "View"], ["Menu", "Menu"], ["Steam", "Steam"], ["QuickAccess", "Quick Access (•••)"]]],
  ["Back grips", [["L4", "L4"], ["L5", "L5"], ["R4", "R4"], ["R5", "R5"]]],
  ["Trackpads", [["LeftPadClick", "Left pad click"], ["RightPadClick", "Right pad click"], ["LeftPadTouch", "Left pad touch"], ["RightPadTouch", "Right pad touch"]]],
  ["Touch sensors", [["LeftStickTouch", "Left stick touch"], ["RightStickTouch", "Right stick touch"], ["LeftGripTouch", "Left grip touch"], ["RightGripTouch", "Right grip touch"]]]
];
const sourceLabels = Object.fromEntries(sourceGroups.flatMap(([, sources]) => sources));

const gamepadTargets = [
  ["None", "Nothing"],
  ["A", "A"], ["B", "B"], ["X", "X"], ["Y", "Y"],
  ["LeftBumper", "Left bumper"], ["RightBumper", "Right bumper"],
  ["Back", "View / Back"], ["Start", "Menu / Start"], ["Guide", "Steam / Guide"],
  ["QuickAccess", "Quick Access / Touchpad"],
  ["LeftStick", "Left stick click"], ["RightStick", "Right stick click"],
  ["DPadUp", "D-pad up"], ["DPadDown", "D-pad down"],
  ["DPadLeft", "D-pad left"], ["DPadRight", "D-pad right"]
];

// KeyboardEvent.code -> Pucky KeyboardKey.
const keyCodes = {
  Escape: "Escape", Enter: "Enter", NumpadEnter: "Enter", Tab: "Tab", Space: "Space",
  Backspace: "Backspace", CapsLock: "CapsLock",
  ShiftLeft: "LeftShift", ShiftRight: "RightShift", ControlLeft: "LeftCtrl", ControlRight: "RightCtrl",
  AltLeft: "LeftAlt", AltRight: "RightAlt", MetaLeft: "LeftMeta", MetaRight: "RightMeta",
  OSLeft: "LeftMeta", OSRight: "RightMeta",
  ArrowUp: "Up", ArrowDown: "Down", ArrowLeft: "Left", ArrowRight: "Right",
  Home: "Home", End: "End", PageUp: "PageUp", PageDown: "PageDown", Insert: "Insert", Delete: "Delete",
  Minus: "Minus", Equal: "Equals", BracketLeft: "LeftBracket", BracketRight: "RightBracket",
  Backslash: "Backslash", Semicolon: "Semicolon", Quote: "Apostrophe", Backquote: "Grave",
  Comma: "Comma", Period: "Period", Slash: "Slash",
  PrintScreen: "PrintScreen", ScrollLock: "ScrollLock", Pause: "Pause",
  AudioVolumeUp: "VolumeUp", AudioVolumeDown: "VolumeDown", AudioVolumeMute: "VolumeMute",
  VolumeUp: "VolumeUp", VolumeDown: "VolumeDown", VolumeMute: "VolumeMute",
  MediaPlayPause: "MediaPlayPause", MediaTrackNext: "MediaNext",
  MediaTrackPrevious: "MediaPrevious", MediaStop: "MediaStop"
};
for (let i = 0; i < 26; i++) {
  const letter = String.fromCharCode(65 + i);
  keyCodes[`Key${letter}`] = letter;
}
for (let i = 0; i <= 9; i++) keyCodes[`Digit${i}`] = `D${i}`;
for (let i = 1; i <= 12; i++) keyCodes[`F${i}`] = `F${i}`;

const keyLabels = {
  LeftCtrl: "Ctrl", RightCtrl: "Right Ctrl", LeftShift: "Shift", RightShift: "Right Shift",
  LeftAlt: "Alt", RightAlt: "Right Alt", LeftMeta: "Win/Cmd", RightMeta: "Right Win/Cmd",
  Apostrophe: "'", Grave: "`", Minus: "-", Equals: "=", LeftBracket: "[", RightBracket: "]",
  Backslash: "\\", Semicolon: ";", Comma: ",", Period: ".", Slash: "/",
  Up: "↑", Down: "↓", Left: "←", Right: "→",
  MouseLeft: "Left click", MouseRight: "Right click", MouseMiddle: "Middle click",
  MouseBack: "Mouse back", MouseForward: "Mouse forward",
  WheelUp: "Wheel up", WheelDown: "Wheel down", WheelLeft: "Wheel left", WheelRight: "Wheel right",
  VolumeUp: "Volume up", VolumeDown: "Volume down", VolumeMute: "Mute",
  MediaPlayPause: "Play/Pause", MediaNext: "Next track", MediaPrevious: "Previous track", MediaStop: "Stop"
};
const modifierKeys = ["LeftCtrl", "RightCtrl", "LeftShift", "RightShift", "LeftAlt", "RightAlt", "LeftMeta", "RightMeta"];
const specialKeys = [
  "MouseLeft", "MouseRight", "MouseMiddle", "MouseBack", "MouseForward",
  "WheelUp", "WheelDown", "WheelLeft", "WheelRight",
  "MediaPlayPause", "MediaNext", "MediaPrevious", "MediaStop", "VolumeUp", "VolumeDown", "VolumeMute",
  "PrintScreen", "Pause", "ScrollLock",
  // Esc cancels key capture, so it is bound from here instead.
  "Escape"
];

let liveSource;

function option(value, label) {
  const element = document.createElement("option");
  element.value = value;
  element.textContent = label;
  return element;
}

function el(tag, className, text) {
  const element = document.createElement(tag);
  if (className) element.className = className;
  if (text != null) element.textContent = text;
  return element;
}

function keyLabel(key) {
  return keyLabels[key] ?? key.replace(/^D(\d)$/, "$1");
}

function chordLabel(keys) {
  return keys?.length ? keys.map(keyLabel).join(" + ") : "";
}

function sortChord(keys) {
  return [...new Set(keys)].sort((a, b) =>
    (modifierKeys.includes(b) ? 1 : 0) - (modifierKeys.includes(a) ? 1 : 0));
}

async function request(path, options = {}) {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 2500);
  try {
    const response = await fetch(`${serviceOrigin}${path}`, {
      cache: "no-store",
      ...options,
      signal: controller.signal
    });
    if (!response.ok) {
      const detail = await response.json().catch(() => null);
      throw new Error(detail?.error ?? detail?.title ?? `${response.status} ${response.statusText}`);
    }
    if (response.status === 204) return null;
    return response.json();
  } finally {
    clearTimeout(timeout);
  }
}

function sendJson(path, method, body) {
  return request(path, {
    method,
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body)
  });
}

/* ---------- Profiles ---------- */

function isDirty() {
  return Boolean(state.profile) && JSON.stringify(readProfile()) !== state.savedSnapshot;
}

function refreshDirty() {
  const dirty = isDirty();
  $("#save-profile").textContent = dirty ? "Save changes" : "Saved";
  $("#save-profile").disabled = !dirty;
  $("#revert-profile").classList.toggle("hidden", !dirty);
  document.title = `${dirty ? "• " : ""}Pucky · Steam Controller`;
}

function renderProfileSelect() {
  const select = $("#profile-select");
  select.replaceChildren(...state.profiles.map(profile => option(profile.id, profile.name)));
  if (state.profile) select.value = state.profile.id;
  $("#delete-profile").disabled = !state.profile || state.profile.id === "default";
}

function renderProfileProblems(problems) {
  const list = $("#profile-problems");
  list.replaceChildren(...problems.map(problem => {
    const item = document.createElement("li");
    const title = el("strong", "", problem.name ? `${problem.name} (${problem.file})` : problem.file);
    const action = problem.loaded
      ? "It still works, but fix this before saving it."
      : "It was not loaded. Fix or remove the file in data/profiles.";
    item.append(title, document.createTextNode(`: ${problem.error} ${action}`));
    return item;
  }));
  list.classList.toggle("hidden", problems.length === 0);
}

async function loadProfiles(selectId) {
  const listing = await request("/api/profiles");
  state.profiles = listing.profiles;
  renderProfileProblems(listing.problems ?? []);
  state.profilesLoaded = true;
  const wanted = selectId ?? state.activeProfileId;
  const profile = state.profiles.find(item => item.id === wanted)
    ?? state.profiles.find(item => item.id === "default")
    ?? state.profiles[0];
  if (profile) populateProfile(profile);
  renderProfileSelect();
}

function populateProfile(profile) {
  state.profile = structuredClone(profile);
  state.profile.keyMappings ??= {};
  state.profile.layers ??= [];
  state.profile.layers.forEach(layer => {
    layer.buttonMappings ??= {};
    layer.keyMappings ??= {};
  });
  if (state.editLayer >= state.profile.layers.length) state.editLayer = -1;

  controls.name.value = profile.name;
  controls.leftDeadzone.value = profile.leftStickDeadzone;
  controls.rightDeadzone.value = profile.rightStickDeadzone;
  controls.leftPadMode.value = profile.leftPad.mode;
  controls.leftPadSensitivity.value = profile.leftPad.sensitivity;
  controls.leftPadDeadzone.value = profile.leftPad.deadzone ?? 0.08;
  controls.leftPadAcceleration.value = profile.leftPad.acceleration ?? 1;
  controls.leftPadClick.checked = Boolean(profile.leftPad.clickRequired);
  controls.rightPadMode.value = profile.rightPad.mode;
  controls.rightPadSensitivity.value = profile.rightPad.sensitivity;
  controls.rightPadDeadzone.value = profile.rightPad.deadzone ?? 0.08;
  controls.rightPadAcceleration.value = profile.rightPad.acceleration ?? 1;
  controls.rightPadClick.checked = Boolean(profile.rightPad.clickRequired);
  controls.leftPadHaptics.checked = profile.leftPad.hapticsEnabled ?? true;
  controls.rightPadHaptics.checked = profile.rightPad.hapticsEnabled ?? true;
  controls.leftHapticIntensity.value = profile.leftPad.hapticIntensity ?? 0.65;
  controls.rightHapticIntensity.value = profile.rightPad.hapticIntensity ?? 0.65;
  controls.gyroMode.value = profile.gyro.mode;
  controls.gyroEnable.value = profile.gyro.enableButton ?? "None";
  controls.gyroSensitivity.value = profile.gyro.sensitivity;
  controls.gyroDeadzone.value = profile.gyro.deadzone ?? 0.02;
  controls.gyroAxis.value = profile.gyro.horizontalAxis ?? "Yaw";
  controls.gyroInvertX.checked = Boolean(profile.gyro.invertX);
  controls.gyroInvertY.checked = Boolean(profile.gyro.invertY);
  state.savedSnapshot = JSON.stringify(readProfile());
  $("#active-profile").textContent = profile.name;
  refreshOutputs();
  renderMappings();
  refreshDirty();
}

function readProfile() {
  const profile = structuredClone(state.profile);
  profile.name = controls.name.value.trim();
  profile.leftStickDeadzone = Number(controls.leftDeadzone.value);
  profile.rightStickDeadzone = Number(controls.rightDeadzone.value);
  Object.assign(profile.leftPad, {
    mode: controls.leftPadMode.value,
    sensitivity: Number(controls.leftPadSensitivity.value),
    deadzone: Number(controls.leftPadDeadzone.value),
    acceleration: Number(controls.leftPadAcceleration.value),
    clickRequired: controls.leftPadClick.checked,
    hapticsEnabled: controls.leftPadHaptics.checked,
    hapticIntensity: Number(controls.leftHapticIntensity.value)
  });
  Object.assign(profile.rightPad, {
    mode: controls.rightPadMode.value,
    sensitivity: Number(controls.rightPadSensitivity.value),
    deadzone: Number(controls.rightPadDeadzone.value),
    acceleration: Number(controls.rightPadAcceleration.value),
    clickRequired: controls.rightPadClick.checked,
    hapticsEnabled: controls.rightPadHaptics.checked,
    hapticIntensity: Number(controls.rightHapticIntensity.value)
  });
  Object.assign(profile.gyro, {
    mode: controls.gyroMode.value,
    enableButton: controls.gyroEnable.value,
    sensitivity: Number(controls.gyroSensitivity.value),
    deadzone: Number(controls.gyroDeadzone.value),
    horizontalAxis: controls.gyroAxis.value,
    invertX: controls.gyroInvertX.checked,
    invertY: controls.gyroInvertY.checked
  });
  return profile;
}

function refreshOutputs() {
  const percent = value => `${Math.round(value * 100)}%`;
  const times = value => `${Number(value).toFixed(1)}×`;
  $("#left-deadzone-value").value = percent(controls.leftDeadzone.value);
  $("#right-deadzone-value").value = percent(controls.rightDeadzone.value);
  $("#left-pad-sensitivity-value").value = times(controls.leftPadSensitivity.value);
  $("#right-pad-sensitivity-value").value = times(controls.rightPadSensitivity.value);
  $("#left-pad-deadzone-value").value = percent(controls.leftPadDeadzone.value);
  $("#right-pad-deadzone-value").value = percent(controls.rightPadDeadzone.value);
  const acceleration = value => Number(value) === 0 ? "Off" : percent(value);
  $("#left-pad-acceleration-value").value = acceleration(controls.leftPadAcceleration.value);
  $("#right-pad-acceleration-value").value = acceleration(controls.rightPadAcceleration.value);
  // Acceleration only applies to pads in mouse mode.
  controls.leftPadAcceleration.disabled = controls.leftPadMode.value !== "Mouse";
  controls.rightPadAcceleration.disabled = controls.rightPadMode.value !== "Mouse";
  $("#left-haptic-value").value = percent(controls.leftHapticIntensity.value);
  $("#right-haptic-value").value = percent(controls.rightHapticIntensity.value);
  $("#gyro-sensitivity-value").value = times(controls.gyroSensitivity.value);
  // Gyro values are a fraction of the IMU's 2000 degrees/second range.
  $("#gyro-deadzone-value").value = `${Math.round(controls.gyroDeadzone.value * 2000)}°/s`;
  const gyroOff = controls.gyroMode.value === "Disabled";
  [controls.gyroEnable, controls.gyroSensitivity, controls.gyroDeadzone, controls.gyroAxis,
    controls.gyroInvertX, controls.gyroInvertY].forEach(control => control.disabled = gyroOff);
}

function uniqueId(name) {
  let slug = name.toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "").slice(0, 48) || "profile";
  const taken = new Set(state.profiles.map(profile => profile.id));
  let id = slug;
  for (let n = 2; taken.has(id); n++) id = `${slug}-${n}`;
  return id;
}

async function saveProfile(profile, message = "Saved and activated") {
  const result = $("#save-result");
  try {
    const saved = await sendJson("/api/profiles", "POST", profile);
    state.activeProfileId = saved.id;
    await loadProfiles(saved.id);
    result.className = "";
    result.textContent = message;
    setTimeout(() => { if (result.textContent === message) result.textContent = ""; }, 2500);
    return true;
  } catch (error) {
    result.className = "error";
    result.textContent = `Could not save: ${error.message}`;
    return false;
  }
}

function confirmDiscard() {
  return !isDirty() || confirm("Discard unsaved changes to this profile?");
}

/* ---------- Mapping editor ---------- */

function editingBindings() {
  const profile = state.profile;
  if (state.editLayer < 0) {
    return { buttons: profile.buttonMappings, keys: profile.keyMappings, layer: null };
  }
  const layer = profile.layers[state.editLayer];
  return { buttons: layer.buttonMappings, keys: layer.keyMappings, layer };
}

function renderLayerTabs() {
  const tabs = $("#layer-tabs");
  const entries = [["Base", -1], ...state.profile.layers.map((layer, index) =>
    [`${layer.name || `Layer ${index + 1}`}${layer.holdButton && layer.holdButton !== "None" ? ` · ${sourceLabels[layer.holdButton] ?? layer.holdButton}` : ""}`, index])];
  tabs.replaceChildren(...entries.map(([label, index]) => {
    const tab = el("button", "layer-tab", label);
    tab.type = "button";
    tab.setAttribute("role", "tab");
    tab.setAttribute("aria-selected", String(index === state.editLayer));
    tab.addEventListener("click", () => {
      state.editLayer = index;
      renderMappings();
    });
    return tab;
  }));

  const layer = state.editLayer >= 0 ? state.profile.layers[state.editLayer] : null;
  $("#layer-settings").classList.toggle("hidden", !layer);
  if (layer) {
    $("#layer-name").value = layer.name ?? "";
    $("#layer-hold").value = layer.holdButton ?? "L4";
  }
}

function renderMappings() {
  if (!state.profile) return;
  renderLayerTabs();
  const { buttons, keys, layer } = editingBindings();
  const table = $("#mapping-table");
  const groups = sourceGroups.map(([groupName, sources]) => {
    const group = el("section", "mapping-group");
    group.append(el("h3", null, groupName));
    sources.forEach(([source, label]) => {
      const row = el("div", "mapping-row");
      row.dataset.source = source;
      if (layer?.holdButton === source) row.classList.add("is-hold");

      const name = el("span", "mapping-source", label);
      const gamepad = document.createElement("select");
      gamepad.setAttribute("aria-label", `${label} gamepad output`);
      if (layer) gamepad.append(option("", "Inherit"));
      gamepad.append(...gamepadTargets.map(([value, text]) => option(value, text)));
      gamepad.value = buttons[source] ?? (layer ? "" : "None");
      gamepad.addEventListener("change", () => {
        if (gamepad.value === "") delete buttons[source];
        else buttons[source] = gamepad.value;
        refreshDirty();
      });

      const keyCell = el("div", "key-cell");
      const chord = keys[source] ?? [];
      const capture = el("button", `key-capture${chord.length ? " bound" : ""}`, chordLabel(chord) || "Bind key…");
      capture.type = "button";
      capture.title = "Click, then press a key or key combination, click a mouse button, or scroll";
      capture.addEventListener("click", event => {
        event.preventDefault();
        startCapture(source, capture);
      });
      const special = document.createElement("select");
      special.className = "key-special";
      special.setAttribute("aria-label", `${label} special key`);
      special.append(option("", "+"), ...specialKeys.map(key => option(key, keyLabel(key))));
      special.addEventListener("change", () => {
        if (!special.value) return;
        setChord(source, sortChord([...(keys[source] ?? []), special.value]).slice(0, 6));
      });
      const clear = el("button", "key-clear", "×");
      clear.type = "button";
      clear.title = "Clear key binding";
      clear.disabled = !chord.length;
      clear.addEventListener("click", () => setChord(source, []));
      keyCell.append(capture, special, clear);

      row.append(name, gamepad, keyCell);
      group.append(row);
    });
    return group;
  });
  table.replaceChildren(...groups);
}

function setChord(source, chord) {
  const { keys } = editingBindings();
  if (chord.length) keys[source] = chord;
  else delete keys[source];
  renderMappings();
  refreshDirty();
}

function startCapture(source, button) {
  stopCapture();
  const pressed = new Set();
  const chord = new Set();
  button.classList.add("capturing");
  button.textContent = "Press keys… (Esc to cancel)";

  const finish = () => {
    stopCapture();
    if (chord.size) setChord(source, sortChord([...chord]).slice(0, 6));
    else renderMappings();
  };
  const onKeyDown = event => {
    event.preventDefault();
    event.stopPropagation();
    if (event.repeat) return;
    if (event.code === "Escape" && pressed.size === 0) {
      chord.clear();
      finish();
      return;
    }
    const key = keyCodes[event.code];
    if (!key) return;
    pressed.add(key);
    chord.add(key);
    button.textContent = chordLabel(sortChord([...chord]));
  };
  const onKeyUp = event => {
    event.preventDefault();
    event.stopPropagation();
    const key = keyCodes[event.code];
    // Ignore keys that were already down when capture started, such as the
    // Enter or Space that activated the button.
    if (!pressed.has(key)) return;
    pressed.delete(key);
    // A chord finishes when its last non-modifier key is released, when a
    // modifier is released after a main key (macOS drops key-up events while
    // Cmd is held), or when a modifier-only chord is fully released.
    const hasMain = [...chord].some(item => !modifierKeys.includes(item));
    const mainStillDown = [...pressed].some(item => !modifierKeys.includes(item));
    if ((hasMain && (!mainStillDown || modifierKeys.includes(key))) || pressed.size === 0) {
      finish();
    }
  };
  const onMouseDown = event => {
    if (event.target === button && event.button === 0 && chord.size === 0) return;
    event.preventDefault();
    event.stopPropagation();
    const mouse = ["MouseLeft", "MouseMiddle", "MouseRight", "MouseBack", "MouseForward"][event.button];
    if (mouse) {
      // The click that follows this press must not also activate whatever
      // was under the pointer.
      const swallow = clickEvent => {
        clickEvent.preventDefault();
        clickEvent.stopPropagation();
      };
      window.addEventListener("click", swallow, { capture: true, once: true });
      window.addEventListener("auxclick", swallow, { capture: true, once: true });
      setTimeout(() => {
        window.removeEventListener("click", swallow, { capture: true });
        window.removeEventListener("auxclick", swallow, { capture: true });
      }, 1000);
      chord.add(mouse);
      finish();
    }
  };
  const onWheel = event => {
    event.preventDefault();
    const key = Math.abs(event.deltaX) > Math.abs(event.deltaY)
      ? (event.deltaX > 0 ? "WheelRight" : "WheelLeft")
      : (event.deltaY > 0 ? "WheelDown" : "WheelUp");
    chord.add(key);
    finish();
  };
  const onContextMenu = event => event.preventDefault();
  const timeout = setTimeout(finish, 8000);

  window.addEventListener("keydown", onKeyDown, true);
  window.addEventListener("keyup", onKeyUp, true);
  window.addEventListener("mousedown", onMouseDown, true);
  window.addEventListener("wheel", onWheel, { capture: true, passive: false });
  window.addEventListener("contextmenu", onContextMenu, true);
  state.capture = () => {
    clearTimeout(timeout);
    window.removeEventListener("keydown", onKeyDown, true);
    window.removeEventListener("keyup", onKeyUp, true);
    window.removeEventListener("mousedown", onMouseDown, true);
    window.removeEventListener("wheel", onWheel, { capture: true });
    window.removeEventListener("contextmenu", onContextMenu, true);
    button.classList.remove("capturing");
  };
}

function stopCapture() {
  state.capture?.();
  state.capture = null;
}

function focusMapping(source) {
  const row = $(`.mapping-row[data-source="${source}"]`);
  if (!row) return;
  row.scrollIntoView({ behavior: "smooth", block: "center" });
  row.classList.remove("flash");
  void row.offsetWidth;
  row.classList.add("flash");
  row.querySelector("select")?.focus({ preventScroll: true });
}

/* ---------- Preferences ---------- */

function populateSettings(settings) {
  state.settings = settings;
  const outputType = $("#output-type");
  [...outputType.options].forEach(item => {
    item.disabled = !settings.supportedOutputTypes.includes(item.value);
    if (item.disabled && !item.textContent.includes("Windows")) item.textContent += " — Windows only";
  });
  outputType.value = settings.outputType;
  $("#handoff-enabled").checked = settings.handOffEnabled;
  $("#handoff-processes").value = settings.handOffProcesses.join(", ");
  $("#start-background").checked = settings.startInBackground;
  $("#launch-login").checked = settings.launchAtLogin;
  $("#launch-login").disabled = !settings.launchAtLoginSupported;
  $("#launch-login-row").title = settings.launchAtLoginSupported
    ? ""
    : "Available in a published build (not when running with dotnet run).";
}

async function updateSettings(change) {
  const result = $("#preferences-result");
  try {
    populateSettings(await sendJson("/api/settings", "PUT", change));
    result.className = "field-note";
    result.textContent = "Saved";
    setTimeout(() => { if (result.textContent === "Saved") result.textContent = ""; }, 1800);
  } catch (error) {
    result.className = "field-note error";
    result.textContent = error.message;
    if (state.settings) populateSettings(state.settings);
  }
}

/* ---------- Live view ---------- */

function copyAxis(value) {
  return value ? { x: value.x ?? 0, y: value.y ?? 0 } : { x: 0, y: 0 };
}

function updateLive(live) {
  const buttons = typeof live?.buttons === "string"
    ? live.buttons.split(", ").filter(button => button && button !== "None")
    : [];

  $$("[data-button]").forEach(element => {
    element.classList.toggle("active", buttons.includes(element.dataset.button));
  });
  $$("[data-touch-button]").forEach(element => {
    element.classList.toggle("active", buttons.includes(element.dataset.touchButton));
  });
  $$(".mapping-row").forEach(row => {
    row.classList.toggle("live", buttons.includes(row.dataset.source));
  });

  state.target.leftStick = copyAxis(live?.leftStick);
  state.target.rightStick = copyAxis(live?.rightStick);
  state.target.leftPad = copyAxis(live?.leftPad?.position);
  state.target.rightPad = copyAxis(live?.rightPad?.position);
  $("#pad-l").classList.toggle("touched", Boolean(live?.leftPad?.touched));
  $("#pad-r").classList.toggle("touched", Boolean(live?.rightPad?.touched));

  const leftTrigger = Math.round((live?.leftTrigger ?? 0) * 100);
  const rightTrigger = Math.round((live?.rightTrigger ?? 0) * 100);
  $("#trigger-l").style.width = `${leftTrigger}%`;
  $("#trigger-r").style.width = `${rightTrigger}%`;
  $("#trigger-l-value").textContent = `${leftTrigger}%`;
  $("#trigger-r-value").textContent = `${rightTrigger}%`;

  const visible = buttons.filter(button => !button.endsWith("Touch"));
  $("#button-list").replaceChildren(...(visible.length ? visible : ["No input"]).map(button => {
    const item = el("span", visible.length ? "pressed" : null, sourceLabels[button] ?? button);
    return item;
  }));
}

function approach(current, target, amount) {
  current.x += (target.x - current.x) * amount;
  current.y += (target.y - current.y) * amount;
}

function renderMotion() {
  approach(state.display.leftStick, state.target.leftStick, 0.38);
  approach(state.display.rightStick, state.target.rightStick, 0.38);
  approach(state.display.leftPad, state.target.leftPad, 0.5);
  approach(state.display.rightPad, state.target.rightPad, 0.5);

  setMotion($("#stick-l i"), state.display.leftStick);
  setMotion($("#stick-r i"), state.display.rightStick);
  setMotion($("#pad-l i"), state.display.leftPad);
  setMotion($("#pad-r i"), state.display.rightPad);
  requestAnimationFrame(renderMotion);
}

function setMotion(element, value) {
  const travel = Math.max(0, (element.parentElement.clientWidth - element.offsetWidth) / 2 - 3);
  element.style.transform = `translate3d(${value.x * travel}px, ${-value.y * travel}px, 0)`;
}

function updateReportRate(connected) {
  const now = performance.now();
  if (now - state.lastReportAt < 1000) return;
  const perSecond = Math.round((state.latestReportCount - state.lastReportCount) * 1000 / (now - state.lastReportAt));
  $("#report-rate").textContent = connected
    ? `${perSecond} reports/s · ${state.latestReportCount.toLocaleString()} total`
    : `${state.latestReportCount.toLocaleString()} reports received`;
  state.lastReportCount = state.latestReportCount;
  state.lastReportAt = now;
}

function updateStatus(status) {
  state.serviceOnline = true;
  state.lastServiceSeen = Date.now();
  state.enabled = status.enabled;
  state.latestReportCount = Math.max(state.latestReportCount, status.reportsReceived);
  const connected = status.controllerConnected;
  const paused = Boolean(status.pausedReason);
  $("#connection-dot").classList.toggle("online", connected && !paused);
  $("#connection-dot").classList.toggle("paused", paused);
  $("#connection-dot").classList.remove("error");
  $("#connection-label").textContent = paused
    ? "Paused"
    : connected ? "Controller connected" : "Service ready";
  $("#connection-detail").textContent = paused
    ? status.pausedReason
    : connected
      ? `${status.device.connection} · ${status.device.name}`
      : "Waiting for USB, Bluetooth, or Puck";
  $("#transport-badge").textContent = paused ? "PAUSED" : connected ? status.device.connection : "WAITING";
  $("#transport-badge").classList.toggle("online", connected && !paused);
  $("#power").classList.toggle("off", !status.enabled);
  $("#power").setAttribute("aria-pressed", String(status.enabled));
  $("#power-label").textContent = status.enabled ? "Mapping on" : "Mapping off";
  $("#device-name").textContent = status.device?.name ?? "Steam Controller";
  $("#input-link").textContent = connected
    ? `${status.device.connection} · ${hex(status.device.vendorId)}:${hex(status.device.productId)}`
    : paused ? "Released" : "Not connected";
  $("#battery").textContent = status.device?.batteryPercent == null
    ? "—"
    : `${status.device.batteryPercent}%${status.device.charging ? " · charging" : ""}`;
  $("#output-name").textContent = status.output;
  $("#output-icon").textContent = status.outputType === "DualShock4" ? "PS" : "X";
  $("#gamepad-link").textContent = status.virtualControllerConnected ? "Online" : "Offline";
  $("#gamepad-link").classList.toggle("good", status.virtualControllerConnected);
  $("#desktop-link").textContent = status.desktopOutput;
  const bridged = connected && status.virtualControllerConnected;
  $("#bridge-state").textContent = paused ? "PAUSED" : bridged ? "BRIDGED" : "OFFLINE";
  $("#bridge-state").classList.toggle("online", bridged);
  $("#vibration-state").textContent = status.vibrationActive ? "Active" : connected ? "Ready" : "Unavailable";
  $("#vibration-state").classList.toggle("good", status.vibrationActive);
  $("#test-vibration").disabled = !connected;
  $("#test-haptics").disabled = !connected;
  $("#version").textContent = `PUCKY ${status.version}`;

  if (state.activeProfileId !== status.activeProfileId) {
    state.activeProfileId = status.activeProfileId;
    // Another window or the tray changed the active profile.
    if (state.profilesLoaded && state.profile?.id !== status.activeProfileId && !isDirty()) {
      loadProfiles(status.activeProfileId).catch(() => {});
    }
  }

  const error = $("#output-error");
  const message = status.lastError ?? status.outputError;
  error.textContent = message ?? "";
  error.classList.toggle("hidden", !message);

  if (!liveSource || liveSource.readyState !== EventSource.OPEN) {
    updateLive(status.liveState);
  }
  updateReportRate(connected);
}

function showServiceOffline(error) {
  state.serviceOnline = false;
  $("#connection-label").textContent = "Controller service unavailable";
  $("#connection-detail").textContent = error?.name === "AbortError"
    ? "The local service did not respond"
    : "Retrying 127.0.0.1:27182";
  $("#connection-dot").classList.remove("online", "paused");
  $("#connection-dot").classList.add("error");
  $("#bridge-state").textContent = "DISCONNECTED";
  $("#bridge-state").classList.remove("online");

  if (managedWindow && Date.now() - state.lastServiceSeen > 1800) {
    liveSource?.close();
    window.close();
  }
}

function connectLiveStream() {
  liveSource?.close();
  liveSource = new EventSource(`${serviceOrigin}/api/live`);
  liveSource.onmessage = event => {
    const frame = JSON.parse(event.data);
    state.latestReportCount = frame.reportsReceived;
    updateLive(frame.state);
  };
}

function hex(value) {
  return Number(value).toString(16).toUpperCase().padStart(4, "0");
}

async function withButton(button, busyText, action, doneDelay = 700) {
  const original = button.textContent;
  try {
    button.disabled = true;
    button.textContent = busyText;
    await action();
    setTimeout(() => {
      button.textContent = original;
      button.disabled = false;
    }, doneDelay);
  } catch (error) {
    button.textContent = error.message;
    setTimeout(() => {
      button.textContent = original;
      button.disabled = !state.serviceOnline;
    }, 1800);
  }
}

/* ---------- Wiring ---------- */

function initialize() {
  const sourceOptions = sourceGroups.flatMap(([, sources]) => sources);
  controls.gyroEnable.replaceChildren(
    option("None", "Always on"),
    ...sourceOptions.map(([value, label]) => option(value, `Holding ${label}`)));
  $("#layer-hold").replaceChildren(...sourceOptions.map(([value, label]) => option(value, label)));

  $$(".controller-stage [data-button]").forEach(marker => {
    marker.classList.add("clickable");
    marker.addEventListener("click", () => focusMapping(marker.dataset.button));
  });

  Object.values(controls).forEach(control => control.addEventListener("input", () => {
    refreshOutputs();
    refreshDirty();
  }));
  controls.name.addEventListener("input", () => {
    $("#active-profile").textContent = controls.name.value || "Untitled";
  });

  $("#power").addEventListener("click", async () => {
    try {
      await request(state.enabled ? "/api/disable" : "/api/enable", { method: "POST" });
    } catch (error) {
      showServiceOffline(error);
    }
  });

  $("#test-vibration").addEventListener("click", event =>
    withButton(event.currentTarget, "Vibrating…", () => request("/api/vibration/test", { method: "POST" }), 750));
  $("#test-haptics").addEventListener("click", event =>
    withButton(event.currentTarget, "Pulsing…", () => request("/api/haptics/test", { method: "POST" })));

  $("#profile-select").addEventListener("change", async event => {
    const id = event.target.value;
    if (!confirmDiscard()) {
      event.target.value = state.profile.id;
      return;
    }
    try {
      const profile = await request(`/api/profiles/${encodeURIComponent(id)}/activate`, { method: "POST" });
      state.activeProfileId = profile.id;
      state.editLayer = -1;
      populateProfile(profile);
      renderProfileSelect();
    } catch (error) {
      $("#save-result").textContent = `Could not activate: ${error.message}`;
    }
  });

  $("#save-profile").addEventListener("click", () => {
    if (!state.profile) return;
    stopCapture();
    saveProfile(readProfile());
  });
  $("#revert-profile").addEventListener("click", () => {
    const saved = state.profiles.find(profile => profile.id === state.profile?.id);
    if (saved) populateProfile(saved);
  });

  $("#new-profile").addEventListener("click", () => {
    if (!confirmDiscard()) return;
    const name = prompt("Name for the new profile:", "New profile");
    if (!name?.trim()) return;
    const base = structuredClone(state.profiles.find(profile => profile.id === "default") ?? state.profile);
    saveProfile({ ...base, id: uniqueId(name), name: name.trim(), layers: [], keyMappings: {} }, "Created");
  });
  $("#duplicate-profile").addEventListener("click", () => {
    if (!state.profile) return;
    const profile = readProfile();
    const name = `${profile.name} copy`;
    saveProfile({ ...profile, id: uniqueId(name), name }, "Duplicated");
  });
  $("#delete-profile").addEventListener("click", async () => {
    if (!state.profile || state.profile.id === "default") return;
    if (!confirm(`Delete the profile "${state.profile.name}"? This cannot be undone.`)) return;
    try {
      await request(`/api/profiles/${encodeURIComponent(state.profile.id)}`, { method: "DELETE" });
      state.activeProfileId = "default";
      state.editLayer = -1;
      await loadProfiles("default");
    } catch (error) {
      $("#save-result").textContent = `Could not delete: ${error.message}`;
    }
  });
  $("#export-profile").addEventListener("click", () => {
    if (!state.profile) return;
    const profile = readProfile();
    const blob = new Blob([JSON.stringify(profile, null, 2)], { type: "application/json" });
    const link = document.createElement("a");
    link.href = URL.createObjectURL(blob);
    link.download = `${profile.id}.pucky.json`;
    link.click();
    setTimeout(() => URL.revokeObjectURL(link.href), 1000);
  });
  $("#import-profile").addEventListener("click", () => {
    if (confirmDiscard()) $("#import-file").click();
  });
  $("#import-file").addEventListener("change", async event => {
    const file = event.target.files?.[0];
    event.target.value = "";
    if (!file) return;
    try {
      const profile = JSON.parse(await file.text());
      if (typeof profile !== "object" || !profile?.name) throw new Error("This file is not a Pucky profile.");
      profile.id = uniqueId(profile.name);
      saveProfile(profile, "Imported");
    } catch (error) {
      $("#save-result").className = "error";
      $("#save-result").textContent = `Could not import: ${error.message}`;
    }
  });

  $("#add-layer").addEventListener("click", () => {
    if (!state.profile) return;
    const used = new Set(state.profile.layers.map(layer => layer.holdButton));
    const hold = ["L4", "L5", "R4", "R5"].find(button => !used.has(button)) ?? "L4";
    state.profile.layers.push({
      name: `Layer ${state.profile.layers.length + 1}`,
      holdButton: hold,
      buttonMappings: {},
      keyMappings: {}
    });
    state.editLayer = state.profile.layers.length - 1;
    renderMappings();
    refreshDirty();
  });
  $("#layer-name").addEventListener("input", event => {
    const { layer } = editingBindings();
    if (!layer) return;
    layer.name = event.target.value;
    renderLayerTabs();
    refreshDirty();
  });
  $("#layer-hold").addEventListener("change", event => {
    const { layer } = editingBindings();
    if (!layer) return;
    layer.holdButton = event.target.value;
    renderMappings();
    refreshDirty();
  });
  $("#delete-layer").addEventListener("click", () => {
    if (state.editLayer < 0) return;
    state.profile.layers.splice(state.editLayer, 1);
    state.editLayer = -1;
    renderMappings();
    refreshDirty();
  });

  $("#output-type").addEventListener("change", event => updateSettings({ outputType: event.target.value }));
  $("#handoff-enabled").addEventListener("change", event => updateSettings({ handOffEnabled: event.target.checked }));
  $("#handoff-processes").addEventListener("change", event => updateSettings({
    handOffProcesses: event.target.value.split(",").map(name => name.trim()).filter(Boolean)
  }));
  $("#start-background").addEventListener("change", event => updateSettings({ startInBackground: event.target.checked }));
  $("#launch-login").addEventListener("change", event => updateSettings({ launchAtLogin: event.target.checked }));
  $("#quit").addEventListener("click", async () => {
    if (!confirm("Quit Pucky? The controller returns to its built-in mouse mode.")) return;
    try {
      await request("/api/quit", { method: "POST" });
    } catch {
      // The service may stop before replying.
    }
  });

  window.addEventListener("beforeunload", event => {
    liveSource?.close();
    if (isDirty()) {
      event.preventDefault();
      event.returnValue = "";
    }
  });
}

async function poll() {
  try {
    const status = await request("/api/status");
    updateStatus(status);
    if (!state.profilesLoaded) {
      await loadProfiles(status.activeProfileId);
    }
    if (!state.settings) {
      populateSettings(await request("/api/settings"));
    }
  } catch (error) {
    showServiceOffline(error);
  } finally {
    setTimeout(poll, state.serviceOnline ? 850 : 350);
  }
}

initialize();
connectLiveStream();
requestAnimationFrame(renderMotion);
poll();
