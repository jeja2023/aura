/* 文件：摄像头页脚本（camera.js） | File: Camera Script */
const apiBase = "https://localhost:5001";
const canvas = document.getElementById("canvas");
const ctx = canvas.getContext("2d");
const resultEl = document.getElementById("result");
const bg = new Image();
const points = [];
let dragIndex = -1;
let bgReady = false;

function getToken() {
  return localStorage.getItem("token") ?? "";
}

function setResult(data) {
  if (!resultEl) return;
  if (typeof data === "string") {
    resultEl.textContent = data;
    return;
  }
  if (data && typeof data === "object") {
    if (typeof data.msg === "string") {
      resultEl.textContent = data.msg;
      return;
    }
    if (Array.isArray(data.data)) {
      resultEl.textContent = `共 ${data.data.length} 条结果`;
      return;
    }
    resultEl.textContent = "操作完成";
    return;
  }
  resultEl.textContent = String(data ?? "");
}

function draw() {
  ctx.clearRect(0, 0, canvas.width, canvas.height);
  if (bgReady) {
    ctx.drawImage(bg, 0, 0, canvas.width, canvas.height);
  } else {
    ctx.fillStyle = "#1f2937";
    ctx.fillRect(0, 0, canvas.width, canvas.height);
  }
  points.forEach((p, i) => {
    ctx.fillStyle = "#ff4d4f";
    ctx.beginPath();
    ctx.arc(p.x, p.y, 6, 0, Math.PI * 2);
    ctx.fill();
    ctx.fillStyle = "#fff";
    ctx.font = "12px sans-serif";
    ctx.fillText(`#${i + 1}`, p.x + 8, p.y - 8);
  });
}

function getPos(e) {
  const rect = canvas.getBoundingClientRect();
  return { x: e.clientX - rect.left, y: e.clientY - rect.top };
}

canvas.addEventListener("mousedown", (e) => {
  const pos = getPos(e);
  dragIndex = points.findIndex((p) => Math.hypot(p.x - pos.x, p.y - pos.y) < 10);
  if (dragIndex < 0) {
    const floorId = Number(document.getElementById("floorId").value);
    const deviceId = Number(document.getElementById("deviceId").value);
    const channelNo = Number(document.getElementById("channelNo").value);
    points.push({ x: pos.x, y: pos.y, floorId, deviceId, channelNo, saved: false });
    draw();
  }
});

canvas.addEventListener("mousemove", (e) => {
  if (dragIndex < 0) return;
  const pos = getPos(e);
  points[dragIndex].x = pos.x;
  points[dragIndex].y = pos.y;
  points[dragIndex].saved = false;
  draw();
});

window.addEventListener("mouseup", () => {
  dragIndex = -1;
});

document.getElementById("loadBgBtn").addEventListener("click", () => {
  const url = document.getElementById("bgUrl").value.trim();
  if (!url) {
    setResult("请输入底图URL");
    return;
  }
  bg.onload = () => {
    bgReady = true;
    draw();
    setResult("底图加载成功");
  };
  bg.onerror = () => setResult("底图加载失败");
  bg.src = url.startsWith("http") ? url : `${apiBase}${url}`;
});

document.getElementById("saveBtn").addEventListener("click", async () => {
  const unsaved = points.filter((p) => !p.saved);
  if (unsaved.length === 0) {
    setResult("没有需要保存的新点位");
    return;
  }
  const out = [];
  for (const p of unsaved) {
    const res = await fetch(`${apiBase}/api/camera/create`, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        Authorization: `Bearer ${getToken()}`
      },
      body: JSON.stringify({
        floorId: p.floorId,
        deviceId: p.deviceId,
        channelNo: p.channelNo,
        posX: Number(p.x.toFixed(2)),
        posY: Number(p.y.toFixed(2))
      })
    });
    const data = await res.json();
    out.push(data);
    if (data.code === 0) p.saved = true;
  }
  setResult(out);
});

document.getElementById("listBtn").addEventListener("click", async () => {
  const res = await fetch(`${apiBase}/api/camera/list`, {
    headers: { Authorization: `Bearer ${getToken()}` }
  });
  const data = await res.json();
  setResult(data);
});

draw();
