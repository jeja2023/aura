/* 文件：首页脚本（index.js） | File: Home Script */
const apiBase = "";
const eventListEl = document.getElementById("events");
const statusBadgeEl = document.getElementById("statusBadge");
const statusTextEl = document.getElementById("statusText");
const statusAiRiskEl = document.getElementById("statusAiRisk");
const statusTimeEl = document.getElementById("statusTime");
const overviewCardsEl = document.getElementById("overviewCards");
const overviewMessageEl = document.getElementById("overviewMessage");
const overviewCaptureEl = document.getElementById("overviewCapture");
const overviewAlertEl = document.getElementById("overviewAlert");
const overviewDeviceEl = document.getElementById("overviewDevice");
const aiFailureRateEl = document.getElementById("aiFailureRate");
const aiRetryQueueEl = document.getElementById("aiRetryQueue");
const aiVectorIssueEl = document.getElementById("aiVectorIssue");
const aiSearchFailureRateEl = document.getElementById("aiSearchFailureRate");
const aiSearchLatencyEl = document.getElementById("aiSearchLatency");
const aiOpsCardsEl = document.getElementById("aiOpsCards");
const aiOpsAlertsEl = document.getElementById("aiOpsAlerts");
let currentAiRiskLevel = "loading";
let previousAiRiskLevel = "loading";
let lastAiRiskToastAt = 0;
let lastAiRiskToastLevel = "loading";
const DEFAULT_AI_RISK_TOAST_COOLDOWN_MS = 5 * 60 * 1000;
const AI_RISK_TOAST_COOLDOWN_MS = readAiRiskToastCooldownMs();

function readAiRiskToastCooldownMs() {
  const pageConfigValue = window.AURA_PAGE_CONFIG?.aiRiskToastCooldownMs;
  const configParsed = Number(pageConfigValue);
  if (Number.isFinite(configParsed) && configParsed >= 0) {
    return Math.floor(configParsed);
  }
  const raw = document.body?.dataset?.aiRiskToastCooldownMs;
  const parsed = Number(raw);
  if (!Number.isFinite(parsed) || parsed < 0) return DEFAULT_AI_RISK_TOAST_COOLDOWN_MS;
  return Math.floor(parsed);
}

function hidePre(preEl) {
  if (!preEl) return;
  preEl.textContent = "";
  preEl.hidden = true;
  preEl.classList.remove("is-error");
}

function setPre(preEl, message, isError) {
  if (!preEl) return;
  if (!message) {
    hidePre(preEl);
    return;
  }

  preEl.textContent = message;
  preEl.hidden = false;
  preEl.classList.toggle("is-error", Boolean(isError));
}

function formatNumber(value) {
  const num = Number(value);
  if (!Number.isFinite(num)) return "0";
  return new Intl.NumberFormat("zh-CN", { maximumFractionDigits: 0 }).format(num);
}

function formatPercent(value) {
  const num = Number(value);
  if (!Number.isFinite(num)) return "0.0%";
  return `${num.toFixed(1)}%`;
}

function formatLatency(value) {
  const num = Number(value);
  if (!Number.isFinite(num)) return "--";
  if (num < 1000) return `${num.toFixed(1)} ms`;
  return `${(num / 1000).toFixed(2)} s`;
}

function setMetricState(el, level) {
  if (!el) return;
  el.classList.remove("is-warning", "is-danger");
  if (level === "danger") el.classList.add("is-danger");
  if (level === "warning") el.classList.add("is-warning");
}

function pickLevel(value, warningThreshold, dangerThreshold) {
  const num = Number(value);
  if (!Number.isFinite(num)) return "normal";
  if (num >= dangerThreshold) return "danger";
  if (num >= warningThreshold) return "warning";
  return "normal";
}

function getRiskRank(level) {
  if (level === "danger") return 3;
  if (level === "warning") return 2;
  if (level === "ok") return 1;
  return 0;
}

function notifyAiRiskUpgrade(nextLevel, alerts) {
  if (!window.aura || typeof window.aura.toast !== "function") return;
  const prevRank = getRiskRank(previousAiRiskLevel);
  const nextRank = getRiskRank(nextLevel);
  if (nextRank <= prevRank || nextRank < 2) return;
  const now = Date.now();
  const inCooldown = lastAiRiskToastLevel === nextLevel && now - lastAiRiskToastAt < AI_RISK_TOAST_COOLDOWN_MS;
  if (inCooldown) return;
  const riskText = nextLevel === "danger" ? "高风险" : "中风险";
  const topAlert = alerts[0] || "请检查AI链路";
  window.aura.toast(`AI链路风险升级为${riskText}：${topAlert}`, true, 3200);
  lastAiRiskToastAt = now;
  lastAiRiskToastLevel = nextLevel;
}

function renderAiOps(ai) {
  const model = ai || {};
  const searchAvailable = Boolean(model.searchAvailable);
  const retryQueueEnabled = model.retryQueueEnabled !== false;
  const alerts = [];

  const failureLevel = pickLevel(model.failureRate, 10, 20);
  if (aiFailureRateEl) aiFailureRateEl.textContent = formatPercent(model.failureRate);
  setMetricState(aiFailureRateEl, failureLevel);
  if (failureLevel !== "normal") alerts.push(`AI失败率偏高（${formatPercent(model.failureRate)}）`);

  const retryLevel = retryQueueEnabled ? pickLevel(model.retryQueuePending, 1, 20) : "normal";
  if (aiRetryQueueEl) aiRetryQueueEl.textContent = retryQueueEnabled ? formatNumber(model.retryQueuePending) : "未启用";
  setMetricState(aiRetryQueueEl, retryLevel);
  if (retryQueueEnabled && retryLevel !== "normal") alerts.push(`补偿队列积压（${formatNumber(model.retryQueuePending)}）`);

  const vectorLevel = pickLevel(model.vectorIssueCount, 1, 10);
  if (aiVectorIssueEl) aiVectorIssueEl.textContent = formatNumber(model.vectorIssueCount);
  setMetricState(aiVectorIssueEl, vectorLevel);
  if (vectorLevel !== "normal") alerts.push(`向量异常增长（${formatNumber(model.vectorIssueCount)}）`);

  const searchFailLevel = searchAvailable ? pickLevel(model.searchFailureRate, 5, 15) : "warning";
  if (aiSearchFailureRateEl) aiSearchFailureRateEl.textContent = searchAvailable ? formatPercent(model.searchFailureRate) : "不可用";
  setMetricState(aiSearchFailureRateEl, searchFailLevel);
  if (searchAvailable && searchFailLevel !== "normal") {
    alerts.push(`检索失败率偏高（${formatPercent(model.searchFailureRate)}）`);
  }
  if (!searchAvailable) {
    alerts.push(`检索指标不可用（${model.searchMessage || "请检查AI服务"}）`);
  }

  const searchLatencyLevel = searchAvailable ? pickLevel(model.searchAvgLatencyMs, 1200, 3000) : "normal";
  if (aiSearchLatencyEl) aiSearchLatencyEl.textContent = searchAvailable ? formatLatency(model.searchAvgLatencyMs) : "不可用";
  setMetricState(aiSearchLatencyEl, searchLatencyLevel);
  if (searchAvailable && searchLatencyLevel !== "normal") {
    alerts.push(`检索延迟偏高（${formatLatency(model.searchAvgLatencyMs)}）`);
  }

  if (aiOpsCardsEl) aiOpsCardsEl.hidden = false;
  if (aiOpsAlertsEl) {
    aiOpsAlertsEl.innerHTML = "";
    for (const message of alerts) {
      const li = document.createElement("li");
      li.textContent = message;
      aiOpsAlertsEl.appendChild(li);
    }
    aiOpsAlertsEl.hidden = alerts.length === 0;
  }
  currentAiRiskLevel = alerts.length > 0
    ? (alerts.length >= 3 ? "danger" : "warning")
    : "ok";
  notifyAiRiskUpgrade(currentAiRiskLevel, alerts);
  previousAiRiskLevel = currentAiRiskLevel;
  if (statusAiRiskEl) {
    statusAiRiskEl.classList.remove("is-ok", "is-warning", "is-danger", "is-loading");
    statusAiRiskEl.classList.add(`is-${currentAiRiskLevel}`);
    const riskText = currentAiRiskLevel === "danger" ? "高风险" : currentAiRiskLevel === "warning" ? "中风险" : "低风险";
    statusAiRiskEl.textContent = `AI链路风险：${riskText}`;
  }
}

function setOverviewCards(totalCapture, totalAlert, onlineDevice, ai) {
  if (overviewCaptureEl) overviewCaptureEl.textContent = String(totalCapture ?? 0);
  if (overviewAlertEl) overviewAlertEl.textContent = String(totalAlert ?? 0);
  if (overviewDeviceEl) overviewDeviceEl.textContent = String(onlineDevice ?? 0);
  renderAiOps(ai);
  if (overviewCardsEl) overviewCardsEl.hidden = false;
  hidePre(overviewMessageEl);
}

function setOverviewError(message) {
  if (overviewCardsEl) overviewCardsEl.hidden = true;
  if (aiOpsCardsEl) aiOpsCardsEl.hidden = true;
  if (aiOpsAlertsEl) {
    aiOpsAlertsEl.innerHTML = "";
    aiOpsAlertsEl.hidden = true;
  }
  currentAiRiskLevel = "loading";
  previousAiRiskLevel = "loading";
  lastAiRiskToastAt = 0;
  lastAiRiskToastLevel = "loading";
  if (statusAiRiskEl) {
    statusAiRiskEl.classList.remove("is-ok", "is-warning", "is-danger", "is-loading");
    statusAiRiskEl.classList.add("is-loading");
    statusAiRiskEl.textContent = "AI链路风险：加载中";
  }
  setPre(overviewMessageEl, message, true);
}

function setStatusView(state, text, timeText) {
  if (statusBadgeEl) {
    statusBadgeEl.classList.remove("is-ok", "is-error", "is-loading");
    statusBadgeEl.classList.add(`is-${state}`);
    statusBadgeEl.textContent = state === "ok" ? "正常" : state === "error" ? "异常" : "加载中";
  }
  if (statusTextEl) statusTextEl.textContent = text || "--";
  if (statusAiRiskEl) {
    statusAiRiskEl.classList.remove("is-ok", "is-warning", "is-danger", "is-loading");
    statusAiRiskEl.classList.add(`is-${currentAiRiskLevel}`);
    const riskText = currentAiRiskLevel === "danger"
      ? "高风险"
      : currentAiRiskLevel === "warning"
        ? "中风险"
        : currentAiRiskLevel === "ok"
          ? "低风险"
          : "加载中";
    statusAiRiskEl.textContent = `AI链路风险：${riskText}`;
  }
  if (statusTimeEl) statusTimeEl.textContent = timeText || "--";
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
  setStatusView("loading", "正在加载系统状态…", "--");
  try {
    const res = await fetch(`${apiBase}/api/health`);
    if (!res.ok) {
      setStatusView("error", `状态加载失败：HTTP ${res.status}`, `更新时间：${formatLocalYMDHMS(new Date())}`);
      return;
    }
    const data = await res.json();
    const message = data?.msg || "系统状态已更新";
    const updateTime = data?.time ? formatTimeYMDHMS(data.time) : formatLocalYMDHMS(new Date());
    const normalized = String(message).toLowerCase();
    const isOk = normalized.includes("正常") || normalized.includes("healthy") || normalized.includes("ok");
    setStatusView(isOk ? "ok" : "loading", message, `更新时间：${updateTime}`);
  } catch (error) {
    setStatusView("error", `状态加载失败：${error.message}`, `更新时间：${formatLocalYMDHMS(new Date())}`);
  }
}

async function loadOverview() {
  if (overviewCardsEl) overviewCardsEl.hidden = false;
  setPre(overviewMessageEl, "正在加载统计概览…", false);
  try {
    const res = await fetch(`${apiBase}/api/stats/overview`, {
      credentials: "include"
    });
    if (res.status === 401) {
      setOverviewError("概览加载失败：登录已失效，请重新登录。");
      return;
    }
    if (!res.ok) {
      setOverviewError(`概览加载失败：HTTP ${res.status}`);
      return;
    }
    const data = await res.json();
    if (data?.code === 0 && data?.data) {
      const d = data.data;
      setOverviewCards(d.totalCapture, d.totalAlert, d.onlineDevice, d.ai);
    } else {
      if (overviewCardsEl) overviewCardsEl.hidden = false;
      setPre(overviewMessageEl, data?.msg || "概览已更新", false);
    }
  } catch (error) {
    setOverviewError(`概览加载失败：${error.message}`);
  }
}

async function initSignalR() {
  if (!window.signalR) {
    pushEvent("signalr.init", { ok: false, msg: "signalr脚本未加载" });
    return;
  }
  const connection = new window.signalR.HubConnectionBuilder()
    .withUrl(`${apiBase}/hubs/events`, {
      withCredentials: true
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
