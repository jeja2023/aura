/* 文件：统计页脚本（stats.js） | File: Stats Script */
const apiBase = "";
const statusEl = document.getElementById("statsStatus");
let dailyChart;
let deviceChart;
let alertChart;
let lastDashboardData = null;
let renderRetryTimer = null;
let renderRetryCount = 0;
const MAX_RENDER_RETRY = 40;

/** 上线展示：成功时不输出原始 JSON，仅错误时显示简短说明 */
function setStatus(message, isError) {
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

function setSuccessHint(message) {
  if (!message) return;
  // 成功提示统一走全局 Toast，不占用页面底部状态栏
  if (window.aura && typeof window.aura.toast === "function") {
    window.aura.toast(message, false, 1800);
  }
  setStatus("");
}

function clearRenderRetryTimer() {
  if (renderRetryTimer != null) {
    clearTimeout(renderRetryTimer);
    renderRetryTimer = null;
  }
}

function normalizeErrorMessage(err) {
  if (!err) return "未知错误";
  if (typeof err === "string") return err;
  if (err.message) return err.message;
  try {
    return String(err);
  } catch {
    return "未知错误";
  }
}

function installGlobalErrorHooks() {
  window.addEventListener("error", (ev) => {
    const msg = ev?.message || normalizeErrorMessage(ev?.error);
    // 常见：CSP 阻止 unsafe-eval / 脚本语法错误 / 资源加载失败
    setStatus(`页面脚本异常：${msg}`, true);
  });
  window.addEventListener("unhandledrejection", (ev) => {
    const msg = normalizeErrorMessage(ev?.reason);
    setStatus(`页面脚本异常：${msg}`, true);
  });
}

function getChartElements() {
  return {
    dailyEl: document.getElementById("dailyChart"),
    deviceEl: document.getElementById("deviceChart"),
    alertEl: document.getElementById("alertChart")
  };
}

function getChartDims() {
  const { dailyEl, deviceEl, alertEl } = getChartElements();
  const fmt = (el) => (el ? `${el.clientWidth}x${el.clientHeight}` : "missing");
  return {
    echarts: window.echarts?.version || "unknown",
    daily: fmt(dailyEl),
    device: fmt(deviceEl),
    alert: fmt(alertEl),
    dailyW: dailyEl ? dailyEl.clientWidth : 0,
    deviceW: deviceEl ? deviceEl.clientWidth : 0,
    alertW: alertEl ? alertEl.clientWidth : 0
  };
}

async function waitForChartLayout(timeoutMs = 2500) {
  const start = Date.now();
  while (Date.now() - start < timeoutMs) {
    const d = getChartDims();
    if (d.dailyW > 0 && d.deviceW > 0 && d.alertW > 0) return true;
    // 等待布局完成（通常 sidebar / grid 在首帧后确定宽度）
    await new Promise((r) => requestAnimationFrame(r));
  }
  return false;
}

function showStatsLoadedHint(data) {
  const daily = data.daily || [];
  const captureSum = daily.reduce((s, x) => s + (Number(x.captureCount) || 0), 0);
  const alertSum = daily.reduce((s, x) => s + (Number(x.alertCount) || 0), 0);
  if (captureSum === 0 && alertSum === 0 && (data.byDevice || []).length === 0 && (data.byAlertType || []).length === 0) {
    if (window.aura && typeof window.aura.toast === "function") {
      window.aura.toast("暂无统计数据（近7日抓拍/告警均为 0）。", false, 2200);
    }
    setStatus("");
    return;
  }
  setSuccessHint(`统计已加载：近7日抓拍${captureSum}次、告警${alertSum}条。`);
}

async function renderWithLayoutRetry(data) {
  const layoutOk = await waitForChartLayout();
  if (layoutOk) {
    const renderedOk = renderCharts(data);
    if (renderedOk) {
      renderRetryCount = 0;
      clearRenderRetryTimer();
      showStatsLoadedHint(data);
    }
    return renderedOk;
  }

  const dims = getChartDims();
  setStatus(`图表容器尺寸异常：daily=${dims.daily}, device=${dims.device}, alert=${dims.alert}（ECharts=${dims.echarts}）`, true);

  if (renderRetryCount < MAX_RENDER_RETRY) {
    renderRetryCount += 1;
    clearRenderRetryTimer();
    renderRetryTimer = setTimeout(() => {
      if (lastDashboardData) {
        renderWithLayoutRetry(lastDashboardData);
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
      fetch(`${apiBase}/api/stats/overview`, {
        credentials: "include"
      }),
      fetch(`${apiBase}/api/stats/dashboard`, {
        credentials: "include"
      })
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

    const data = dashboard.data || {};
    lastDashboardData = data;
    await renderWithLayoutRetry(data);
  } catch (error) {
    setStatus(`查询失败：${error.message}`, true);
  }
}

function ensureCharts() {
  if (!window.echarts) {
    setStatus("图表组件未加载，请确认 frontend/common/vendor/echarts.min.js 存在。", true);
    return false;
  }
  try {
    dailyChart = dailyChart || window.echarts.init(document.getElementById("dailyChart"));
    deviceChart = deviceChart || window.echarts.init(document.getElementById("deviceChart"));
    alertChart = alertChart || window.echarts.init(document.getElementById("alertChart"));
    return true;
  } catch (err) {
    setStatus(`图表初始化失败：${normalizeErrorMessage(err)}（常见原因：浏览器策略/CSP 拦截 unsafe-eval）`, true);
    return false;
  }
}

function renderCharts(data) {
  if (!ensureCharts()) return false;
  const daily = data.daily || [];
  const byDevice = data.byDevice || [];
  const byAlertType = data.byAlertType || [];
  try {
    const { dailyEl, deviceEl, alertEl } = getChartElements();
    const dims = getChartDims();

    dailyChart.setOption({
      tooltip: { trigger: "axis" },
      legend: { data: ["抓拍", "告警"] },
      xAxis: { type: "category", data: daily.map((x) => x.day) },
      yAxis: { type: "value" },
      series: [
        {
          name: "抓拍",
          type: "line",
          data: daily.map((x) => x.captureCount),
          smooth: true,
          itemStyle: { color: "#2563eb" }
        },
        {
          name: "告警",
          type: "line",
          data: daily.map((x) => x.alertCount),
          smooth: true,
          itemStyle: { color: "#f59e0b" }
        }
      ]
    });
    deviceChart.setOption({
      tooltip: { trigger: "axis" },
      xAxis: { type: "category", data: byDevice.map((x) => `D${x.deviceId}`) },
      yAxis: { type: "value" },
      series: [{ type: "bar", data: byDevice.map((x) => x.count), itemStyle: { color: "#2563eb" } }]
    });
    alertChart.setOption({
      tooltip: { trigger: "item" },
      color: ["#2563eb", "#f59e0b", "#10b981", "#8b5cf6", "#ec4899", "#64748b"],
      series: [
        {
          type: "pie",
          radius: "62%",
          data: byAlertType.map((x) => ({ name: x.alertType, value: x.count }))
        }
      ]
    });
    // 主动触发一次 resize，避免容器首次布局导致尺寸为 0 的极端情况
    dailyChart.resize();
    deviceChart.resize();
    alertChart.resize();

    // 如果仍然没有图层节点，给出诊断信息（避免“看似成功但实际空白”）
    const dailyLayers = dailyEl ? dailyEl.querySelectorAll("canvas, svg").length : 0;
    const deviceLayers = deviceEl ? deviceEl.querySelectorAll("canvas, svg").length : 0;
    const alertLayers = alertEl ? alertEl.querySelectorAll("canvas, svg").length : 0;
    if (dailyLayers + deviceLayers + alertLayers === 0) {
      setStatus(`图表未生成渲染层：daily=${dailyLayers}, device=${deviceLayers}, alert=${alertLayers}（ECharts=${dims.echarts}）`, true);
      return false;
    }
    return true;
  } catch (err) {
    setStatus(`图表渲染失败：${normalizeErrorMessage(err)}（常见原因：浏览器策略/CSP 拦截 unsafe-eval）`, true);
    return false;
  }
}

window.addEventListener("resize", () => {
  dailyChart?.resize();
  deviceChart?.resize();
  alertChart?.resize();
  if (lastDashboardData) {
    clearRenderRetryTimer();
    renderRetryTimer = setTimeout(() => {
      renderWithLayoutRetry(lastDashboardData);
    }, 50);
  }
});

document.getElementById("load").addEventListener("click", load);
installGlobalErrorHooks();
load();
