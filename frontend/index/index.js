/* 文件：首页脚本（index.js） | File: Home Script */
const apiBase = "";
const eventListEl = document.getElementById("events");
const statusEl = document.getElementById("status");
const overviewEl = document.getElementById("overview");

const SUCCESS_STATUS_MS = 5000;
let statusSuccessTimer = null;
let overviewSuccessTimer = null;

function clearStatusTimer(timerRefName) {
  if (timerRefName === "status") {
    if (statusSuccessTimer != null) clearTimeout(statusSuccessTimer);
    statusSuccessTimer = null;
    return;
  }
  if (timerRefName === "overview") {
    if (overviewSuccessTimer != null) clearTimeout(overviewSuccessTimer);
    overviewSuccessTimer = null;
  }
}

function hidePre(preEl) {
  if (!preEl) return;
  preEl.textContent = "";
  preEl.hidden = true;
  preEl.classList.remove("is-error");
}

function setPre(preEl, message, isError, timerTarget) {
  if (!preEl) return;
  if (!message) {
    hidePre(preEl);
    clearStatusTimer(timerTarget);
    return;
  }

  preEl.textContent = message;
  preEl.hidden = false;
  preEl.classList.toggle("is-error", Boolean(isError));

  clearStatusTimer(timerTarget);
  if (!isError) {
    const timerSetter = (fn) => {
      if (timerTarget === "status") statusSuccessTimer = fn();
      if (timerTarget === "overview") overviewSuccessTimer = fn();
    };
    timerSetter(() => window.setTimeout(() => {
      if (timerTarget === "status") statusSuccessTimer = null;
      if (timerTarget === "overview") overviewSuccessTimer = null;
      hidePre(preEl);
    }, SUCCESS_STATUS_MS));
  }
}

function formatPayload(payload) {
  if (!payload) return "";
  if (typeof payload === "string") return payload;
  if (typeof payload === "object") {
    if (typeof payload.msg === "string") return payload.msg;
    if (typeof payload.detail === "string") return payload.detail;
    if (typeof payload.eventType === "string") return payload.eventType;
    if (payload.data && typeof payload.data === "object" && typeof payload.data.detail === "string") return payload.data.detail;
    return "";
  }
  return String(payload);
}

function localizeEventName(name) {
  // SignalR 事件在前端统一为中文可读文案，避免展示英文 key
  switch (name) {
    case "signalr.connected":
      return "SignalR已连接";
    case "signalr.init":
      return "SignalR初始化";
    case "capture.received":
      return "抓拍事件";
    case "alert.created":
      return "告警事件";
    case "track.event":
      return "轨迹事件";
    case "judge.updated":
      return "研判事件";
    default:
      return name;
  }
}

function formatTimeYMDHMS(time) {
  if (typeof window.formatDateTimeDisplay === "function") {
    return window.formatDateTimeDisplay(time, "");
  }
  const raw = String(time ?? "");
  const m = raw.match(/^(\d{4}-\d{2}-\d{2})T(\d{2}:\d{2}:\d{2})/);
  if (m) return `${m[1]} ${m[2]}`;
  const d = new Date(time);
  if (Number.isNaN(d.getTime())) return raw.replace("T", " ");
  const pad = (n) => String(n).padStart(2, "0");
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(
    d.getMinutes()
  )}:${pad(d.getSeconds())}`;
}

function formatLocalYMDHMS(date) {
  const pad = (n) => String(n).padStart(2, "0");
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${pad(date.getHours())}:${pad(
    date.getMinutes()
  )}:${pad(date.getSeconds())}`;
}

function pushEvent(name, payload) {
  if (!eventListEl) return;
  const li = document.createElement("li");
  const time = formatLocalYMDHMS(new Date());
  const tail = formatPayload(payload);
  li.textContent = `[${time}] ${localizeEventName(name)}${tail ? " " + tail : ""}`;
  eventListEl.prepend(li);
  while (eventListEl.children.length > 80) {
    eventListEl.removeChild(eventListEl.lastChild);
  }
}

async function loadStatus() {
  setPre(statusEl, "", false, "status");
  try {
    const res = await fetch(`${apiBase}/api/health`);
    if (!res.ok) {
      setPre(statusEl, `状态加载失败：HTTP ${res.status}`, true, "status");
      return;
    }
    const data = await res.json();
    const message = data?.msg
      ? `${data.msg}（${data.time ? formatTimeYMDHMS(data.time) : ""}）`
      : "系统状态已更新";
    setPre(statusEl, message, false, "status");
  } catch (error) {
    setPre(statusEl, `状态加载失败：${error.message}`, true, "status");
  }
}

async function loadOverview() {
  setPre(overviewEl, "", false, "overview");
  try {
    const res = await fetch(`${apiBase}/api/stats/overview`, {
      credentials: "include"
    });
    if (res.status === 401) {
      setPre(overviewEl, "概览加载失败：登录已失效，请重新登录。", true, "overview");
      return;
    }
    if (!res.ok) {
      setPre(overviewEl, `概览加载失败：HTTP ${res.status}`, true, "overview");
      return;
    }
    const data = await res.json();
    if (data?.code === 0 && data?.data) {
      const d = data.data;
      setPre(
        overviewEl,
        `抓拍合计：${d.totalCapture ?? 0} 条；告警合计：${d.totalAlert ?? 0} 条；在线设备：${d.onlineDevice ?? 0} 台`,
        false,
        "overview"
      );
    } else {
      setPre(overviewEl, data?.msg || "概览已更新", false, "overview");
    }
  } catch (error) {
    setPre(overviewEl, `概览加载失败：${error.message}`, true, "overview");
  }
}

async function initSignalR() {
  if (!window.signalR) {
    pushEvent("signalr.init", { ok: false, msg: "signalr脚本未加载" });
    return;
  }
  const connection = new window.signalR.HubConnectionBuilder()
    .withUrl(`${apiBase}/hubs/events`, {
      // 纯 Cookie 会话下由浏览器自动携带 HttpOnly Cookie；accessTokenFactory 保持兼容占位
      accessTokenFactory: () => ""
    })
    .withAutomaticReconnect()
    .build();

  connection.on("capture.received", (d) => pushEvent("capture.received", d));
  connection.on("alert.created", (d) => pushEvent("alert.created", d));
  connection.on("track.event", (d) => pushEvent("track.event", d));
  connection.on("judge.updated", (d) => pushEvent("judge.updated", d));

  try {
    await connection.start();
    pushEvent("signalr.connected", { ok: true });
  } catch (error) {
    pushEvent("signalr.connected", { ok: false, msg: error.message });
  }
}

function bindEvents() {
  const refreshBtn = document.getElementById("refreshBtn");
  if (refreshBtn) {
    refreshBtn.addEventListener("click", async () => {
      await Promise.all([loadStatus(), loadOverview()]);
    });
  }
}

async function bootstrap() {
  bindEvents();
  await Promise.all([loadStatus(), loadOverview()]);
  await initSignalR();
}

bootstrap();
