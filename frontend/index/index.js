/* 文件：首页脚本（index.js） | File: Home Script */
const apiBase = "https://localhost:5001";
const eventListEl = document.getElementById("events");

function getToken() {
  return localStorage.getItem("token") ?? "";
}

function syncNavByLoginState() {
  const loginNavItem = document.getElementById("loginNavItem");
  if (!loginNavItem) return;
  loginNavItem.style.display = getToken() ? "none" : "";
}

function pushEvent(name, payload) {
  if (!eventListEl) return;
  const li = document.createElement("li");
  const time = new Date().toLocaleTimeString();
  li.textContent = `[${time}] ${name} ${JSON.stringify(payload)}`;
  eventListEl.prepend(li);
  while (eventListEl.children.length > 80) {
    eventListEl.removeChild(eventListEl.lastChild);
  }
}

async function loadStatus() {
  const statusEl = document.getElementById("status");
  try {
    const res = await fetch(`${apiBase}/api/health`);
    if (!res.ok) {
      statusEl.textContent = `状态加载失败：HTTP ${res.status}`;
      return;
    }
    const data = await res.json();
    statusEl.textContent = JSON.stringify(data, null, 2);
  } catch (error) {
    statusEl.textContent = `状态加载失败：${error.message}`;
  }
}

async function loadOverview() {
  const overviewEl = document.getElementById("overview");
  try {
    const res = await fetch(`${apiBase}/api/stats/overview`, {
      headers: { Authorization: `Bearer ${getToken()}` }
    });
    if (res.status === 401) {
      overviewEl.textContent = "概览加载失败：登录已失效，请重新登录。";
      return;
    }
    if (!res.ok) {
      overviewEl.textContent = `概览加载失败：HTTP ${res.status}`;
      return;
    }
    const data = await res.json();
    overviewEl.textContent = JSON.stringify(data, null, 2);
  } catch (error) {
    overviewEl.textContent = `概览加载失败：${error.message}`;
  }
}

async function initSignalR() {
  if (!window.signalR) {
    pushEvent("signalr.init", { ok: false, msg: "signalr脚本未加载" });
    return;
  }
  const connection = new window.signalR.HubConnectionBuilder()
    .withUrl(`${apiBase}/hubs/events`)
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
  refreshBtn.addEventListener("click", async () => {
    await Promise.all([loadStatus(), loadOverview()]);
  });

  const logoutBtn = document.getElementById("logoutBtn");
  logoutBtn.addEventListener("click", () => {
    localStorage.removeItem("token");
    document.cookie = "aura_token=; path=/; Max-Age=0; SameSite=Lax";
    window.location.href = "/login/";
  });
}

async function bootstrap() {
  syncNavByLoginState();
  bindEvents();
  await Promise.all([loadStatus(), loadOverview()]);
  await initSignalR();
}

bootstrap();
