/* 文件：轨迹页脚本（track.js） | File: Track Script */
const apiBase = "https://localhost:5001";
const resultEl = document.getElementById("result");
const canvas = document.getElementById("canvas");
const ctx = canvas.getContext("2d");
let route = [];
let timer = null;
let step = 0;

function getToken() {
  return localStorage.getItem("token") ?? "";
}

function setResult(data) {
  resultEl.textContent = typeof data === "string" ? data : JSON.stringify(data, null, 2);
}

async function load() {
  const vid = document.getElementById("vid").value.trim();
  setResult("加载中...");

  if (!vid) {
    setResult("请输入VID");
    return;
  }

  try {
    const [trackRes, cameraRes] = await Promise.all([
      fetch(`${apiBase}/api/track/${encodeURIComponent(vid)}`, {
        headers: { Authorization: `Bearer ${getToken()}` }
      }),
      fetch(`${apiBase}/api/camera/list`, {
        headers: { Authorization: `Bearer ${getToken()}` }
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
    setResult({ raw: trackData, route });
  } catch (error) {
    setResult(`查询失败：${error.message}`);
  }
}

function drawRoute() {
  ctx.clearRect(0, 0, canvas.width, canvas.height);
  ctx.fillStyle = "#1f2937";
  ctx.fillRect(0, 0, canvas.width, canvas.height);

  if (route.length > 0) {
    ctx.strokeStyle = "#00d2ff";
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
