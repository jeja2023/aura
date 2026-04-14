/* 文件：摄像头页脚本（camera.js） | File: Camera Script */
const apiBase = "";
const canvas = document.getElementById("canvas");
const ctx = canvas.getContext("2d");
const resultEl = document.getElementById("result");
const bg = new Image();
const points = [];
let dragIndex = -1;
let bgReady = false;

function showToast(message, isError = false) {
  const text = String(message ?? "").trim();
  if (!text) return;
  if (window.aura && typeof window.aura.toast === "function") {
    window.aura.toast(text, isError);
    return;
  }
  setResult(text);
}

function isErrorText(text) {
  return /失败|错误|异常|请输入|无权限|超时|断开/.test(String(text ?? ""));
}

function setResult(data) {
  if (!resultEl) return;
  if (typeof data === "string") {
    if (isErrorText(data)) {
      resultEl.textContent = data;
      return;
    }
    showToast(data, false);
    return;
  }
  if (data && typeof data === "object") {
    if (typeof data.msg === "string") {
      if (isErrorText(data.msg)) {
        resultEl.textContent = data.msg;
      } else {
        showToast(data.msg, false);
      }
      return;
    }
    if (Array.isArray(data.data)) {
      showToast(`共 ${data.data.length} 条结果`, false);
      return;
    }
    showToast("操作完成", false);
    return;
  }
  const text = String(data ?? "");
  if (isErrorText(text)) {
    resultEl.textContent = text;
    return;
  }
  showToast(text, false);
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
    showToast("底图加载成功");
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
      credentials: "include",
      headers: {
        "Content-Type": "application/json"
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
  const okCount = out.filter((x) => x && x.code === 0).length;
  const failCount = out.length - okCount;
  if (okCount > 0) showToast(`保存完成：成功 ${okCount} 条${failCount > 0 ? `，失败 ${failCount} 条` : ""}`);
  if (failCount > 0) setResult(`部分保存失败：失败 ${failCount} 条`);
});

document.getElementById("listBtn").addEventListener("click", async () => {
  try {
    const res = await fetch(`${apiBase}/api/camera/list`, {
      credentials: "include"
    });
    const data = await res.json();
    if (!res.ok || data?.code !== 0) {
      setResult(data?.msg || "查询失败");
      return;
    }
    const count = Array.isArray(data?.data) ? data.data.length : 0;
    showToast(`查询成功，共 ${count} 条`);
  } catch (error) {
    setResult(`查询失败：${error.message}`);
  }
});

draw();
