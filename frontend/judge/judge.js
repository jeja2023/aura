/* 文件：研判页脚本（judge.js） | File: Judge Script */
const apiBase = "https://localhost:5001";
const resultEl = document.getElementById("result");

function getToken() {
  return localStorage.getItem("token") ?? "";
}

function setResult(data) {
  resultEl.textContent = typeof data === "string" ? data : JSON.stringify(data, null, 2);
}

function getDate() {
  const val = document.getElementById("date").value;
  if (val) return val;
  const now = new Date();
  const y = now.getFullYear();
  const m = String(now.getMonth() + 1).padStart(2, "0");
  const d = String(now.getDate()).padStart(2, "0");
  return `${y}-${m}-${d}`;
}

async function post(path, body) {
  const res = await fetch(`${apiBase}${path}`, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      Authorization: `Bearer ${getToken()}`
    },
    body: JSON.stringify(body)
  });
  return await res.json();
}

async function load() {
  setResult("加载中...");

  try {
    const date = getDate();
    const res = await fetch(`${apiBase}/api/judge/daily?date=${encodeURIComponent(date)}`, {
      headers: { Authorization: `Bearer ${getToken()}` }
    });
    const data = await res.json();
    setResult(data);
  } catch (error) {
    setResult(`查询失败：${error.message}`);
  }
}

async function runDaily() {
  setResult("执行中...");
  try {
    const date = getDate();
    const cutoffHour = Number(document.getElementById("cutoffHour").value);
    const data = await post("/api/judge/run/daily", { date, cutoffHour });
    setResult(data);
  } catch (error) {
    setResult(`执行失败：${error.message}`);
  }
}

async function runHome() {
  setResult("执行中...");
  try {
    const data = await post("/api/judge/run/home", { date: getDate() });
    setResult(data);
  } catch (error) {
    setResult(`执行失败：${error.message}`);
  }
}

async function runAbnormal() {
  setResult("执行中...");
  try {
    const data = await post("/api/judge/run/abnormal", {
      date: getDate(),
      groupThreshold: 2,
      stayMinutes: 120
    });
    setResult(data);
  } catch (error) {
    setResult(`执行失败：${error.message}`);
  }
}

async function runNight() {
  setResult("执行中...");
  try {
    const cutoffHour = Number(document.getElementById("cutoffHour").value);
    const data = await post("/api/judge/run/night", { date: getDate(), cutoffHour });
    setResult(data);
  } catch (error) {
    setResult(`执行失败：${error.message}`);
  }
}

document.getElementById("load").addEventListener("click", load);
document.getElementById("runDaily").addEventListener("click", runDaily);
document.getElementById("runHome").addEventListener("click", runHome);
document.getElementById("runAbnormal").addEventListener("click", runAbnormal);
document.getElementById("runNight").addEventListener("click", runNight);
