/* 文件：统计页脚本（stats.js） | File: Stats Script */
const apiBase = "";
const statusEl = document.getElementById("statsStatus");
let dailyChart;
let deviceChart;
let alertChart;
/** 成功提示自动消失定时器 */
let successStatusTimer = null;
const SUCCESS_STATUS_MS = 5000;

function clearSuccessStatusTimer() {
  if (successStatusTimer != null) {
    clearTimeout(successStatusTimer);
    successStatusTimer = null;
  }
}

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

async function load() {
  clearSuccessStatusTimer();
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

    renderCharts(dashboard.data || {});
    const od = overview.data || {};
    const cap = od.totalCapture ?? 0;
    const al = od.totalAlert ?? 0;
    const on = od.onlineDevice ?? 0;
    setStatus(`图表已根据最新数据刷新：抓拍合计 ${cap} 条、告警合计 ${al} 条，在线设备 ${on} 台。`, false);
    clearSuccessStatusTimer();
    successStatusTimer = window.setTimeout(() => {
      successStatusTimer = null;
      setStatus("");
    }, SUCCESS_STATUS_MS);
  } catch (error) {
    setStatus(`查询失败：${error.message}`, true);
  }
}

function ensureCharts() {
  if (!window.echarts) {
    setStatus("图表组件未加载，请确认 frontend/common/vendor/echarts.min.js 存在。", true);
    return false;
  }
  dailyChart = dailyChart || window.echarts.init(document.getElementById("dailyChart"));
  deviceChart = deviceChart || window.echarts.init(document.getElementById("deviceChart"));
  alertChart = alertChart || window.echarts.init(document.getElementById("alertChart"));
  return true;
}

function renderCharts(data) {
  if (!ensureCharts()) return;
  const daily = data.daily || [];
  const byDevice = data.byDevice || [];
  const byAlertType = data.byAlertType || [];
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
}

window.addEventListener("resize", () => {
  dailyChart?.resize();
  deviceChart?.resize();
  alertChart?.resize();
});

document.getElementById("load").addEventListener("click", load);
load();
