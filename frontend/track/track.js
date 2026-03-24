/* 文件：轨迹页脚本（track.js） | File: Track Script */
const apiBase = "https://localhost:5001";
const resultEl = document.getElementById("result");
const canvas = document.getElementById("canvas");
const ctx = canvas.getContext("2d");
let route = [];
let timer = null;
let step = 0;

/** 成功提示自动消失定时器 */
let successStatusTimer = null;
const SUCCESS_STATUS_MS = 5000;

function clearSuccessStatusTimer() {
  if (successStatusTimer != null) {
    clearTimeout(successStatusTimer);
    successStatusTimer = null;
  }
}

function hideResult() {
  if (!resultEl) return;
  resultEl.textContent = "";
  resultEl.hidden = true;
  resultEl.classList.remove("is-error");
}

function deriveMessage(data) {
  if (typeof data === "string") return data;
  if (data && typeof data === "object") {
    if (typeof data.msg === "string") return data.msg;
    if (Array.isArray(data.data)) return `共 ${data.data.length} 条结果`;
    return "操作完成";
  }
  return String(data ?? "");
}

function isErrorPayload(data, message) {
  if (data && typeof data === "object" && typeof data.code === "number") {
    return data.code !== 0;
  }
  if (typeof message === "string") {
    return /失败|错误|异常|请/.test(message);
  }
  return false;
}

function setResult(data) {
  if (!resultEl) return;

  const isEmpty = !data || (typeof data === "string" && data.trim() === "");
  if (isEmpty) {
    clearSuccessStatusTimer();
    hideResult();
    return;
  }

  const message = deriveMessage(data);
  const isError = isErrorPayload(data, message);

  clearSuccessStatusTimer();
  resultEl.textContent = message;
  resultEl.hidden = false;
  resultEl.classList.toggle("is-error", isError);

  if (!isError) {
    successStatusTimer = window.setTimeout(() => {
      successStatusTimer = null;
      hideResult();
    }, SUCCESS_STATUS_MS);
  }
}

async function load() {
  const vid = document.getElementById("vid").value.trim();
  setResult("");

  if (!vid) {
    setResult("请输入人员虚拟编号");
    return;
  }

  try {
    const [trackRes, cameraRes] = await Promise.all([
      fetch(`${apiBase}/api/track/${encodeURIComponent(vid)}?limit=500`, {
        credentials: "include"
      }),
      fetch(`${apiBase}/api/camera/list`, {
        credentials: "include"
      })
    ]);
    const trackData = await trackRes.json();
    const cameraData = await cameraRes.json();
    const camMap = new Map((cameraData.data || []).map((x) => [x.cameraId, x]));
    const points = (trackData.data?.points || [])
      .map((p) => {
        const c = camMap.get(p.cameraId);
        return c ? { x: Number(c.posX), y: Number(c.posY), cameraId: p.cameraId, roiId: p.roiId, time: p.time } : null;
      })
      .filter(Boolean);
    route = points.reverse();
    step = 0;
    drawRoute();
    setResult(`轨迹加载成功：共 ${route.length} 个点。`);
  } catch (error) {
    setResult(`查询失败：${error.message}`);
  }
}

function drawRoute() {
  ctx.clearRect(0, 0, canvas.width, canvas.height);
  ctx.fillStyle = "#1f2937";
  ctx.fillRect(0, 0, canvas.width, canvas.height);

  if (route.length > 0) {
    ctx.strokeStyle = "#2563eb";
    ctx.lineWidth = 2;
    ctx.beginPath();
    ctx.moveTo(route[0].x, route[0].y);
    for (let i = 1; i < route.length; i++) {
      ctx.lineTo(route[i].x, route[i].y);
    }
    ctx.stroke();
  }

  for (let i = 0; i < route.length; i++) {
    const p = route[i];
    ctx.fillStyle = i <= step ? "#ff4d4f" : "#8ca3bf";
    ctx.beginPath();
    ctx.arc(p.x, p.y, i === step ? 7 : 4, 0, Math.PI * 2);
    ctx.fill();
  }
}

function play() {
  if (route.length === 0) {
    setResult("请先查询轨迹");
    return;
  }
  if (timer) clearInterval(timer);
  timer = setInterval(() => {
    step++;
    if (step >= route.length) {
      step = route.length - 1;
      clearInterval(timer);
      timer = null;
    }
    drawRoute();
  }, 600);
}

function pause() {
  if (timer) clearInterval(timer);
  timer = null;
}

document.getElementById("play").addEventListener("click", play);
document.getElementById("pause").addEventListener("click", pause);
document.getElementById("load").addEventListener("click", load);
drawRoute();
