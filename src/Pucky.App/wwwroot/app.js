const $ = selector => document.querySelector(selector);
const $$ = selector => [...document.querySelectorAll(selector)];
const serviceOrigin = location.protocol === "file:" ? "http://127.0.0.1:27182" : "";
const managedWindow = new URLSearchParams(location.search).get("puckyWindow") === "1";

const state = {
  enabled: true,
  profile: null,
  profiles: [],
  profilesLoaded: false,
  serviceOnline: false,
  lastServiceSeen: Date.now(),
  lastReportCount: 0,
  lastReportAt: performance.now(),
  latestReportCount: 0,
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
  leftDeadzone: $("#left-deadzone"),
  rightDeadzone: $("#right-deadzone"),
  leftPadMode: $("#left-pad-mode"),
  leftPadSensitivity: $("#left-pad-sensitivity"),
  rightPadMode: $("#right-pad-mode"),
  rightPadSensitivity: $("#right-pad-sensitivity"),
  leftPadHaptics: $("#left-pad-haptics"),
  rightPadHaptics: $("#right-pad-haptics"),
  leftHapticIntensity: $("#left-haptic-intensity"),
  rightHapticIntensity: $("#right-haptic-intensity"),
  gyroMode: $("#gyro-mode"),
  gyroSensitivity: $("#gyro-sensitivity")
};

const rearMappingControls = $$('[data-mapping-source]');
const mappingTargets = [
  ["None", "Disabled"],
  ["A", "A"], ["B", "B"], ["X", "X"], ["Y", "Y"],
  ["LeftBumper", "Left bumper"], ["RightBumper", "Right bumper"],
  ["Back", "View / Back"], ["Start", "Menu / Start"], ["Guide", "Steam / Guide"],
  ["QuickAccess", "Quick Access / Options"],
  ["LeftStick", "Left stick click"], ["RightStick", "Right stick click"],
  ["DPadUp", "D-pad up"], ["DPadDown", "D-pad down"],
  ["DPadLeft", "D-pad left"], ["DPadRight", "D-pad right"]
];

rearMappingControls.forEach(select => {
  select.replaceChildren(...mappingTargets.map(([value, label]) => {
    const option = document.createElement("option");
    option.value = value;
    option.textContent = label;
    return option;
  }));
});

let liveSource;

async function request(path, options = {}) {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 1800);
  try {
    const response = await fetch(`${serviceOrigin}${path}`, {
      cache: "no-store",
      ...options,
      signal: controller.signal
    });
    if (!response.ok) {
      const detail = await response.json().catch(() => null);
      throw new Error(detail?.error ?? `${response.status} ${response.statusText}`);
    }
    if (response.status === 204) return null;
    return response.json();
  } finally {
    clearTimeout(timeout);
  }
}

async function loadProfiles() {
  const profiles = await request("/api/profiles");
  state.profiles = profiles;
  state.profilesLoaded = true;
  const select = $("#profile-select");
  select.replaceChildren(...profiles.map(profile => {
    const option = document.createElement("option");
    option.value = profile.id;
    option.textContent = profile.name;
    return option;
  }));
  const active = profiles.find(profile => profile.name === $("#active-profile").textContent);
  state.profile = active ?? profiles.find(profile => profile.id === "default") ?? profiles[0];
  if (state.profile) {
    select.value = state.profile.id;
    populateProfile(state.profile);
  }
}

function populateProfile(profile) {
  state.profile = structuredClone(profile);
  controls.leftDeadzone.value = profile.leftStickDeadzone;
  controls.rightDeadzone.value = profile.rightStickDeadzone;
  controls.leftPadMode.value = profile.leftPad.mode;
  controls.leftPadSensitivity.value = profile.leftPad.sensitivity;
  controls.rightPadMode.value = profile.rightPad.mode;
  controls.rightPadSensitivity.value = profile.rightPad.sensitivity;
  controls.leftPadHaptics.checked = profile.leftPad.hapticsEnabled ?? true;
  controls.rightPadHaptics.checked = profile.rightPad.hapticsEnabled ?? true;
  controls.leftHapticIntensity.value = profile.leftPad.hapticIntensity ?? 0.65;
  controls.rightHapticIntensity.value = profile.rightPad.hapticIntensity ?? 0.65;
  controls.gyroMode.value = profile.gyro.mode;
  controls.gyroSensitivity.value = profile.gyro.sensitivity;
  rearMappingControls.forEach(select => {
    select.value = profile.buttonMappings?.[select.dataset.mappingSource] ?? "None";
  });
  refreshOutputs();
}

function readProfile() {
  const profile = structuredClone(state.profile);
  profile.leftStickDeadzone = Number(controls.leftDeadzone.value);
  profile.rightStickDeadzone = Number(controls.rightDeadzone.value);
  profile.leftPad.mode = controls.leftPadMode.value;
  profile.leftPad.sensitivity = Number(controls.leftPadSensitivity.value);
  profile.leftPad.hapticsEnabled = controls.leftPadHaptics.checked;
  profile.leftPad.hapticIntensity = Number(controls.leftHapticIntensity.value);
  profile.rightPad.mode = controls.rightPadMode.value;
  profile.rightPad.sensitivity = Number(controls.rightPadSensitivity.value);
  profile.rightPad.hapticsEnabled = controls.rightPadHaptics.checked;
  profile.rightPad.hapticIntensity = Number(controls.rightHapticIntensity.value);
  profile.gyro.mode = controls.gyroMode.value;
  profile.gyro.sensitivity = Number(controls.gyroSensitivity.value);
  profile.buttonMappings ??= {};
  rearMappingControls.forEach(select => {
    const source = select.dataset.mappingSource;
    if (select.value === "None") {
      delete profile.buttonMappings[source];
    } else {
      profile.buttonMappings[source] = select.value;
    }
  });
  return profile;
}

function refreshOutputs() {
  $("#left-deadzone-value").value = `${Math.round(controls.leftDeadzone.value * 100)}%`;
  $("#right-deadzone-value").value = `${Math.round(controls.rightDeadzone.value * 100)}%`;
  $("#left-pad-sensitivity-value").value = `${Number(controls.leftPadSensitivity.value).toFixed(1)}×`;
  $("#right-pad-sensitivity-value").value = `${Number(controls.rightPadSensitivity.value).toFixed(1)}×`;
  $("#left-haptic-value").value = `${Math.round(controls.leftHapticIntensity.value * 100)}%`;
  $("#right-haptic-value").value = `${Math.round(controls.rightHapticIntensity.value * 100)}%`;
  $("#gyro-sensitivity-value").value = `${Number(controls.gyroSensitivity.value).toFixed(1)}×`;
}

function copyAxis(value) {
  return value ? { x: value.x ?? 0, y: value.y ?? 0 } : { x: 0, y: 0 };
}

function updateLive(live) {
  const buttons = typeof live?.buttons === "string"
    ? live.buttons.split(", ").filter(Boolean)
    : [];

  $$("[data-button]").forEach(element => {
    element.classList.toggle("active", buttons.includes(element.dataset.button));
  });
  $$("[data-touch-button]").forEach(element => {
    element.classList.toggle("active", buttons.includes(element.dataset.touchButton));
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
    const item = document.createElement("span");
    item.textContent = button;
    if (visible.length) item.className = "pressed";
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

  setMotion($("#stick-l i"), state.display.leftStick, true);
  setMotion($("#stick-r i"), state.display.rightStick, true);
  setMotion($("#pad-l i"), state.display.leftPad, true);
  setMotion($("#pad-r i"), state.display.rightPad, true);
  requestAnimationFrame(renderMotion);
}

function setMotion(element, value, invertY) {
  const travel = Math.max(0, (element.parentElement.clientWidth - element.offsetWidth) / 2 - 3);
  const y = (invertY ? -value.y : value.y) * travel;
  element.style.transform = `translate3d(${value.x * travel}px, ${y}px, 0)`;
}

function updateReportRate(connected) {
  const now = performance.now();
  if (now - state.lastReportAt < 1000) return;
  const perSecond = state.latestReportCount - state.lastReportCount;
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
  $("#connection-dot").classList.toggle("online", connected);
  $("#connection-dot").classList.remove("error");
  $("#connection-label").textContent = connected ? "Controller connected" : "Service ready";
  $("#connection-detail").textContent = connected
    ? `${status.device.connection} · ${status.device.name}`
    : "Waiting for USB, Bluetooth, or Puck";
  $("#transport-badge").textContent = connected ? status.device.connection : "WAITING";
  $("#transport-badge").classList.toggle("online", connected);
  $("#power").classList.toggle("off", !status.enabled);
  $("#power").setAttribute("aria-pressed", String(status.enabled));
  $("#power-label").textContent = status.enabled ? "Mapping on" : "Mapping off";
  $("#active-profile").textContent = status.activeProfile;
  $("#device-name").textContent = status.device?.name ?? "Steam Controller";
  $("#input-link").textContent = connected
    ? `${status.device.connection} · ${hex(status.device.vendorId)}:${hex(status.device.productId)}`
    : "Not connected";
  $("#battery").textContent = status.device?.batteryPercent == null
    ? "—"
    : `${status.device.batteryPercent}%${status.device.charging ? " · charging" : ""}`;
  $("#output-name").textContent = status.output;
  $("#gamepad-link").textContent = status.virtualControllerConnected ? "Online" : "Offline";
  $("#gamepad-link").classList.toggle("good", status.virtualControllerConnected);
  $("#desktop-link").textContent = status.desktopOutput;
  $("#bridge-state").textContent = connected && status.virtualControllerConnected ? "BRIDGED" : "OFFLINE";
  $("#bridge-state").classList.toggle("online", connected && status.virtualControllerConnected);
  $("#vibration-state").textContent = status.vibrationActive ? "Active" : connected ? "Ready" : "Unavailable";
  $("#vibration-state").classList.toggle("good", status.vibrationActive);
  $("#test-vibration").disabled = !connected;
  $("#test-haptics").disabled = !connected;

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
  $("#connection-dot").classList.remove("online");
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

$("#power").addEventListener("click", async () => {
  try {
    await request(state.enabled ? "/api/disable" : "/api/enable", { method: "POST" });
  } catch (error) {
    showServiceOffline(error);
  }
});

$("#test-vibration").addEventListener("click", async event => {
  const button = event.currentTarget;
  const original = button.textContent;
  try {
    button.disabled = true;
    button.textContent = "Vibrating…";
    await request("/api/vibration/test", { method: "POST" });
    setTimeout(() => {
      button.textContent = original;
      button.disabled = false;
    }, 750);
  } catch (error) {
    button.textContent = error.message;
    setTimeout(() => {
      button.textContent = original;
      button.disabled = !state.serviceOnline;
    }, 1800);
  }
});

$("#test-haptics").addEventListener("click", async event => {
  const button = event.currentTarget;
  const original = button.textContent;
  try {
    button.disabled = true;
    button.textContent = "Pulsing both pads…";
    await request("/api/haptics/test", { method: "POST" });
    setTimeout(() => {
      button.textContent = original;
      button.disabled = false;
    }, 700);
  } catch (error) {
    button.textContent = error.message;
    setTimeout(() => {
      button.textContent = original;
      button.disabled = !state.serviceOnline;
    }, 1800);
  }
});

$("#profile-select").addEventListener("change", async event => {
  try {
    const profile = await request(`/api/profiles/${encodeURIComponent(event.target.value)}/activate`, {
      method: "POST"
    });
    populateProfile(profile);
  } catch (error) {
    $("#save-result").textContent = `Could not activate: ${error.message}`;
  }
});

$("#save-profile").addEventListener("click", async () => {
  const message = $("#save-result");
  if (!state.profile) {
    message.textContent = "Profile data is still loading.";
    return;
  }
  try {
    state.profile = await request("/api/profiles", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(readProfile())
    });
    message.textContent = "Saved and activated";
    setTimeout(() => message.textContent = "", 2500);
  } catch (error) {
    message.textContent = `Could not save: ${error.message}`;
  }
});

Object.values(controls).forEach(control => control.addEventListener("input", refreshOutputs));
rearMappingControls.forEach(control => control.addEventListener("input", refreshOutputs));

async function poll() {
  try {
    const status = await request("/api/status");
    updateStatus(status);
    if (!state.profilesLoaded) {
      await loadProfiles();
    }
  } catch (error) {
    showServiceOffline(error);
  } finally {
    setTimeout(poll, state.serviceOnline ? 850 : 350);
  }
}

window.addEventListener("beforeunload", () => liveSource?.close());
connectLiveStream();
requestAnimationFrame(renderMotion);
poll();
