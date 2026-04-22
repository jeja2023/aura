/* 文件：统计页脚本（stats.js）| File: Stats Script */
const apiBase = "";
const statusEl = document.getElementById("statsStatus");

const overviewEls = {
  totalCapture: document.getElementById("metricTotalCapture"),
  totalAlert: document.getElementById("metricTotalAlert"),
  onlineDevice: document.getElementById("metricOnlineDevice"),
  aiFailureRate: document.getElementById("metricAiFailureRate"),
  aiFailureNote: document.getElementById("metricAiFailureNote"),
  retryQueue: document.getElementById("metricRetryQueue"),
  retryQueueNote: document.getElementById("metricRetryQueueNote"),
  vectorIssue: document.getElementById("metricVectorIssue"),
  vectorIssueNote: document.getElementById("metricVectorIssueNote"),
  searchFailureRate: document.getElementById("metricSearchFailureRate"),
  searchFailureNote: document.getElementById("metricSearchFailureNote"),
  searchLatency: document.getElementById("metricSearchLatency"),
  searchLatencyNote: document.getElementById("metricSearchLatencyNote")
};

let dailyChart;
let deviceChart;
let alertChart;
let aiStatusChart;
let aiDailyChart;
let lastDashboardData = null;
let renderRetryTimer = null;
let renderRetryCount = 0;
const MAX_RENDER_RETRY = 40;

function setStatus(message, isError = false) {
  if (!statusEl) return;
  if (!message) {
    statusEl.textContent = "";
    statusEl.hidden = true;
    statusEl.classList.remove("is-error");
    return;
  }
  statusEl.textContent = message;
  statusEl.hidden = false;
  statusEl.classList.toggle("is-error", Boolean(isError));
}

function clearRenderRetryTimer() {
  if (renderRetryTimer != null) {
    clearTimeout(renderRetryTimer);
    renderRetryTimer = null;
  }
}

function normalizeErrorMessage(error) {
  if (!error) return "未知错误";
  if (typeof error === "string") return error;
  if (error.message) return error.message;
  try {
    return String(error);
  } catch {
    return "未知错误";
  }
}

function installGlobalErrorHooks() {
  window.addEventListener("error", (event) => {
    const message = event?.message || normalizeErrorMessage(event?.error);
    setStatus(`页面脚本异常：${message}`, true);
  });
  window.addEventListener("unhandledrejection", (event) => {
    setStatus(`页面脚本异常：${normalizeErrorMessage(event?.reason)}`, true);
  });
}

function formatNumber(value) {
  const num = Number(value);
  if (!Number.isFinite(num)) return "-";
  return new Intl.NumberFormat("zh-CN", { maximumFractionDigits: 0 }).format(num);
}

function formatPercent(value) {
  const num = Number(value);
  if (!Number.isFinite(num)) return "-";
  return `${num.toFixed(1)}%`;
}

function formatLatency(value) {
  const num = Number(value);
  if (!Number.isFinite(num)) return "-";
  if (num < 1000) return `${num.toFixed(1)} ms`;
  return `${(num / 1000).toFixed(2)} s`;
}

function getChartElements() {
  return {
    dailyEl: document.getElementById("dailyChart"),
    deviceEl: document.getElementById("deviceChart"),
    alertEl: document.getElementById("alertChart"),
    aiStatusEl: document.getElementById("aiStatusChart"),
    aiDailyEl: document.getElementById("aiDailyChart")
  };
}

function getChartDims() {
  const { dailyEl, deviceEl, alertEl, aiStatusEl, aiDailyEl } = getChartElements();
  const fmt = (el) => (el ? `${el.clientWidth}x${el.clientHeight}` : "missing");
  return {
    echarts: window.echarts?.version || "unknown",
    daily: fmt(dailyEl),
    device: fmt(deviceEl),
    alert: fmt(alertEl),
    aiStatus: fmt(aiStatusEl),
    aiDaily: fmt(aiDailyEl),
    widths: [
      dailyEl?.clientWidth || 0,
      deviceEl?.clientWidth || 0,
      alertEl?.clientWidth || 0,
      aiStatusEl?.clientWidth || 0,
      aiDailyEl?.clientWidth || 0
    ]
  };
}

async function waitForChartLayout(timeoutMs = 2500) {
  const start = Date.now();
  while (Date.now() - start < timeoutMs) {
    const dims = getChartDims();
    if (dims.widths.every((width) => width > 0)) return true;
    await new Promise((resolve) => requestAnimationFrame(resolve));
  }
  return false;
}

function updateMetricValue(element, value) {
  if (element) element.textContent = value;
}

function renderOverview(data) {
  const overview = data || {};
  const ai = overview.ai || {};

  updateMetricValue(overviewEls.totalCapture, formatNumber(overview.totalCapture));
  updateMetricValue(overviewEls.totalAlert, formatNumber(overview.totalAlert));
  updateMetricValue(overviewEls.onlineDevice, formatNumber(overview.onlineDevice));

  updateMetricValue(overviewEls.aiFailureRate, formatPercent(ai.failureRate));
  updateMetricValue(overviewEls.retryQueue, formatNumber(ai.retryQueuePending));
  updateMetricValue(overviewEls.vectorIssue, formatNumber(ai.vectorIssueCount));
  updateMetricValue(
    overviewEls.searchFailureRate,
    ai.searchAvailable ? formatPercent(ai.searchFailureRate) : "不可用"
  );
  updateMetricValue(
    overviewEls.searchLatency,
    ai.searchAvailable ? formatLatency(ai.searchAvgLatencyMs) : "不可用"
  );

  if (overviewEls.aiFailureNote) {
    overviewEls.aiFailureNote.textContent = `近 ${formatNumber(ai.captureWindowDays || 7)} 天 ${formatNumber(ai.failureCount)} / ${formatNumber(ai.trackedCaptureTotal)} 条失败`;
  }
  if (overviewEls.retryQueueNote) {
    overviewEls.retryQueueNote.textContent = ai.retryQueueEnabled
      ? "当前待处理重试任务"
      : "重试队列未启用";
  }
  if (overviewEls.vectorIssueNote) {
    overviewEls.vectorIssueNote.textContent = `近 7 天异常 ${formatNumber(ai.vectorIssueCount)} 条，异常率 ${formatPercent(ai.vectorIssueRate)}`;
  }
  if (overviewEls.searchFailureNote) {
    overviewEls.searchFailureNote.textContent = ai.searchAvailable
      ? `最近 ${formatNumber(ai.searchWindowMinutes)} 分钟 ${formatNumber(ai.searchFailed)} / ${formatNumber(ai.searchTotal)} 次失败`
      : (ai.searchMessage || "AI 检索指标暂不可用");
  }
  if (overviewEls.searchLatencyNote) {
    overviewEls.searchLatencyNote.textContent = ai.searchAvailable
      ? `空结果 ${formatNumber(ai.searchEmpty)} 次，消息：${ai.searchMessage || "正常"}`
      : (ai.searchMessage || "AI 检索指标暂不可用");
  }
}

function ensureCharts() {
  if (!window.echarts) {
    setStatus("图表组件未加载，请确认 frontend/common/vendor/echarts.min.js 可用。", true);
    return false;
  }

  try {
    const { dailyEl, deviceEl, alertEl, aiStatusEl, aiDailyEl } = getChartElements();
    dailyChart = dailyChart || window.echarts.init(dailyEl);
    deviceChart = deviceChart || window.echarts.init(deviceEl);
    alertChart = alertChart || window.echarts.init(alertEl);
    aiStatusChart = aiStatusChart || window.echarts.init(aiStatusEl);
    aiDailyChart = aiDailyChart || window.echarts.init(aiDailyEl);
    return true;
  } catch (error) {
    setStatus(`图表初始化失败：${normalizeErrorMessage(error)}`, true);
    return false;
  }
}

function renderCharts(data) {
  if (!ensureCharts()) return false;

  const daily = data?.daily || [];
  const byDevice = data?.byDevice || [];
  const byAlertType = data?.byAlertType || [];
  const aiStatus = data?.aiStatus || [];
  const aiDaily = data?.aiDaily || [];

  const palette = {
    blue: "#2563eb",
    amber: "#f59e0b",
    green: "#10b981",
    red: "#ef4444",
    slate: "#64748b"
  };

  try {
    dailyChart.setOption({
      tooltip: { trigger: "axis" },
      legend: { data: ["抓拍", "告警"] },
      grid: { left: 44, right: 24, top: 40, bottom: 28 },
      xAxis: { type: "category", data: daily.map((item) => item.day) },
      yAxis: { type: "value" },
      series: [
        {
          name: "抓拍",
          type: "line",
          smooth: true,
          data: daily.map((item) => item.captureCount),
          itemStyle: { color: palette.blue },
          areaStyle: { color: "rgba(37, 99, 235, 0.10)" }
        },
        {
          name: "告警",
          type: "line",
          smooth: true,
          data: daily.map((item) => item.alertCount),
          itemStyle: { color: palette.amber },
          areaStyle: { color: "rgba(245, 158, 11, 0.10)" }
        }
      ]
    });

    deviceChart.setOption({
      tooltip: { trigger: "axis" },
      grid: { left: 44, right: 24, top: 20, bottom: 44 },
      xAxis: { type: "category", data: byDevice.map((item) => `D${item.deviceId}`), axisLabel: { interval: 0, rotate: 30 } },
      yAxis: { type: "value" },
      series: [
        {
          type: "bar",
          data: byDevice.map((item) => item.count),
          itemStyle: { color: palette.blue },
          barMaxWidth: 34
        }
      ]
    });

    alertChart.setOption({
      tooltip: { trigger: "item" },
      color: [palette.blue, palette.amber, palette.green, "#8b5cf6", "#ec4899", palette.slate],
      series: [
        {
          type: "pie",
          radius: ["38%", "68%"],
          avoidLabelOverlap: true,
          label: { formatter: "{b}\n{d}%" },
          data: byAlertType.map((item) => ({ name: item.alertType, value: item.count }))
        }
      ]
    });

    aiStatusChart.setOption({
      tooltip: { trigger: "item" },
      color: [palette.green, palette.slate, palette.amber, "#fb7185", palette.red, "#b91c1c"],
      series: [
        {
          type: "pie",
          radius: ["34%", "70%"],
          label: { formatter: "{b}\n{d}%" },
          data: aiStatus.map((item) => ({ name: item.label, value: item.count }))
        }
      ]
    });

    aiDailyChart.setOption({
      tooltip: { trigger: "axis", axisPointer: { type: "shadow" } },
      legend: { data: ["就绪", "补偿中", "失败", "仅提特征"] },
      grid: { left: 44, right: 24, top: 40, bottom: 28 },
      xAxis: { type: "category", data: aiDaily.map((item) => item.day) },
      yAxis: { type: "value" },
      series: [
        {
          name: "就绪",
          type: "bar",
          stack: "ai",
          data: aiDaily.map((item) => item.readyCount),
          itemStyle: { color: palette.green }
        },
        {
          name: "补偿中",
          type: "bar",
          stack: "ai",
          data: aiDaily.map((item) => item.retryPendingCount),
          itemStyle: { color: palette.amber }
        },
        {
          name: "失败",
          type: "bar",
          stack: "ai",
          data: aiDaily.map((item) => item.failedCount),
          itemStyle: { color: palette.red }
        },
        {
          name: "仅提特征",
          type: "bar",
          stack: "ai",
          data: aiDaily.map((item) => item.extractOnlyCount),
          itemStyle: { color: palette.slate }
        }
      ]
    });

    dailyChart.resize();
    deviceChart.resize();
    alertChart.resize();
    aiStatusChart.resize();
    aiDailyChart.resize();

    const { dailyEl, deviceEl, alertEl, aiStatusEl, aiDailyEl } = getChartElements();
    const renderedLayers =
      (dailyEl?.querySelectorAll("canvas, svg").length || 0) +
      (deviceEl?.querySelectorAll("canvas, svg").length || 0) +
      (alertEl?.querySelectorAll("canvas, svg").length || 0) +
      (aiStatusEl?.querySelectorAll("canvas, svg").length || 0) +
      (aiDailyEl?.querySelectorAll("canvas, svg").length || 0);

    if (renderedLayers === 0) {
      setStatus("图表未生成渲染层，请检查 ECharts 加载状态。", true);
      return false;
    }

    return true;
  } catch (error) {
    setStatus(`图表渲染失败：${normalizeErrorMessage(error)}`, true);
    return false;
  }
}

function showStatsLoadedHint(overview, dashboard) {
  const businessDaily = dashboard?.daily || [];
  const captureSum = businessDaily.reduce((sum, item) => sum + (Number(item.captureCount) || 0), 0);
  const alertSum = businessDaily.reduce((sum, item) => sum + (Number(item.alertCount) || 0), 0);
  const ai = overview?.ai || {};
  const aiFailures = Number(ai.failureCount) || 0;

  if (window.aura && typeof window.aura.toast === "function") {
    window.aura.toast(`统计已刷新：近 7 日抓拍 ${captureSum} 次、告警 ${alertSum} 条、AI 失败 ${aiFailures} 条`, false, 2200);
  }
}

async function renderWithLayoutRetry(overview, dashboard) {
  const layoutReady = await waitForChartLayout();
  if (layoutReady) {
    renderOverview(overview);
    const rendered = renderCharts(dashboard);
    if (rendered) {
      renderRetryCount = 0;
      clearRenderRetryTimer();
      showStatsLoadedHint(overview, dashboard);
    }
    return rendered;
  }

  const dims = getChartDims();
  setStatus(
    `图表容器尺寸异常：daily=${dims.daily}, device=${dims.device}, alert=${dims.alert}, aiStatus=${dims.aiStatus}, aiDaily=${dims.aiDaily}, ECharts=${dims.echarts}`,
    true
  );

  if (renderRetryCount < MAX_RENDER_RETRY) {
    renderRetryCount += 1;
    clearRenderRetryTimer();
    renderRetryTimer = setTimeout(() => {
      if (lastDashboardData) {
        void renderWithLayoutRetry(lastDashboardData.overview, lastDashboardData.dashboard);
      }
    }, 200);
  }

  return false;
}

async function load() {
  clearRenderRetryTimer();
  renderRetryCount = 0;
  setStatus("");

  try {
    const [overviewRes, dashboardRes] = await Promise.all([
      fetch(`${apiBase}/api/stats/overview`, { credentials: "include" }),
      fetch(`${apiBase}/api/stats/dashboard`, { credentials: "include" })
    ]);

    const overview = await overviewRes.json();
    const dashboard = await dashboardRes.json();

    if (!overviewRes.ok) {
      setStatus(`概览请求失败：HTTP ${overviewRes.status}`, true);
      return;
    }
    if (!dashboardRes.ok) {
      setStatus(`图表数据请求失败：HTTP ${dashboardRes.status}`, true);
      return;
    }
    if (overview.code !== 0) {
      setStatus(overview.msg || "概览查询失败", true);
      return;
    }
    if (dashboard.code !== 0) {
      setStatus(dashboard.msg || "图表数据查询失败", true);
      return;
    }

    lastDashboardData = {
      overview: overview.data || {},
      dashboard: dashboard.data || {}
    };

    await renderWithLayoutRetry(lastDashboardData.overview, lastDashboardData.dashboard);
  } catch (error) {
    setStatus(`查询失败：${normalizeErrorMessage(error)}`, true);
  }
}

window.addEventListener("resize", () => {
  dailyChart?.resize();
  deviceChart?.resize();
  alertChart?.resize();
  aiStatusChart?.resize();
  aiDailyChart?.resize();

  if (lastDashboardData) {
    clearRenderRetryTimer();
    renderRetryTimer = setTimeout(() => {
      void renderWithLayoutRetry(lastDashboardData.overview, lastDashboardData.dashboard);
    }, 50);
  }
});

document.getElementById("load")?.addEventListener("click", load);
installGlobalErrorHooks();
void load();
