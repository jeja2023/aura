/* 文件：统计页脚本（stats.js） | File: Stats Script */
const apiBase = "https://localhost:5001";
const resultEl = document.getElementById("result");
let dailyChart;
let deviceChart;
let alertChart;

function getToken() {
  return localStorage.getItem("token") ?? "";
}

function setResult(data) {
  resultEl.textContent = typeof data === "string" ? data : JSON.stringify(data, null, 2);
}

async function load() {
  setResult("加载中...");

  try {
    const [overviewRes, dashboardRes] = await Promise.all([
      fetch(`${apiBase}/api/stats/overview`, {
        headers: { Authorization: `Bearer ${getToken()}` }
      }),
      fetch(`${apiBase}/api/stats/dashboard`, {
        headers: { Authorization: `Bearer ${getToken()}` }
      })
    ]);
    const overview = await overviewRes.json();
    const dashboard = await dashboardRes.json();
    renderCharts(dashboard.data || {});
    setResult({ overview, dashboard });
  } catch (error) {
    setResult(`查询失败：${error.message}`);
  }
}

function ensureCharts() {
  if (!window.echarts) {
    setResult("ECharts 未加载");
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
      { name: "抓拍", type: "line", data: daily.map((x) => x.captureCount), smooth: true },
      { name: "告警", type: "line", data: daily.map((x) => x.alertCount), smooth: true }
    ]
  });
  deviceChart.setOption({
    tooltip: { trigger: "axis" },
    xAxis: { type: "category", data: byDevice.map((x) => `D${x.deviceId}`) },
    yAxis: { type: "value" },
    series: [{ type: "bar", data: byDevice.map((x) => x.count), itemStyle: { color: "#00d2ff" } }]
  });
  alertChart.setOption({
    tooltip: { trigger: "item" },
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
