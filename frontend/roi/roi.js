/* 文件：防区页脚本（roi.js） | File: ROI Script */
const apiBase = "";
const resultEl = document.getElementById("result");
const canvas = document.getElementById("canvas");
const ctx = canvas.getContext("2d");
const bg = new Image();
const points = [];
let bgReady = false;

let successStatusTimer = null;
const SUCCESS_STATUS_MS = 5000;
const defaultPromptText = resultEl?.textContent ?? "";

function clearSuccessStatusTimer() {
  if (successStatusTimer != null) {
    clearTimeout(successStatusTimer);
    successStatusTimer = null;
  }
}

function setResultText(message, isError) {
  if (!resultEl) return;
  resultEl.textContent = message;
  resultEl.classList.toggle("is-error", Boolean(isError));
}

function deriveMessage(data) {
  if (typeof data === "string") return data;
  if (data && typeof data === "object") {
    if (typeof data.msg === "string") return data.msg;
    if (Array.isArray(data.data)) return `共 ${data.data.length} 条结果`;
    if (Array.isArray(data.points)) return `已更新点位，共 ${data.points.length} 个点`;
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
    setResultText(defaultPromptText, false);
    return;
  }

  const message = deriveMessage(data);
  const isError = isErrorPayload(data, message);

  clearSuccessStatusTimer();
  setResultText(message, isError);

  if (!isError) {
    successStatusTimer = window.setTimeout(() => {
      successStatusTimer = null;
      setResultText(defaultPromptText, false);
    }, SUCCESS_STATUS_MS);
  }
}

async function load() {
  // 不显示“加载中...”常驻占位，避免干扰画布提示
  setResult("");

  try {
    const res = await fetch(`${apiBase}/api/roi/list`, {
      credentials: "include"
    });
    const data = await res.json();
    setResult(data);
  } catch (error) {
    setResult(`查询失败：${error.message}`);
  }
}

function draw() {
  ctx.clearRect(0, 0, canvas.width, canvas.height);
  if (bgReady) {
    ctx.drawImage(bg, 0, 0, canvas.width, canvas.height);
  } else {
    ctx.fillStyle = "#1f2937";
    ctx.fillRect(0, 0, canvas.width, canvas.height);
  }

  if (points.length > 0) {
    ctx.strokeStyle = "#2563eb";
    ctx.lineWidth = 2;
    ctx.beginPath();
    ctx.moveTo(points[0].x, points[0].y);
    for (let i = 1; i < points.length; i++) {
      ctx.lineTo(points[i].x, points[i].y);
    }
    if (points.length >= 3) ctx.lineTo(points[0].x, points[0].y);
    ctx.stroke();
  }

  points.forEach((p, i) => {
    ctx.fillStyle = "#ff4d4f";
    ctx.beginPath();
    ctx.arc(p.x, p.y, 5, 0, Math.PI * 2);
    ctx.fill();
    ctx.fillStyle = "#fff";
    ctx.fillText(`${i + 1}`, p.x + 6, p.y - 6);
  });
}

function getPos(e) {
  const rect = canvas.getBoundingClientRect();
  return { x: e.clientX - rect.left, y: e.clientY - rect.top };
}

canvas.addEventListener("click", (e) => {
  const pos = getPos(e);
  points.push({ x: Number(pos.x.toFixed(2)), y: Number(pos.y.toFixed(2)) });
  draw();
  setResult({ points });
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

document.getElementById("undoBtn").addEventListener("click", () => {
  points.pop();
  draw();
  setResult({ points });
});

document.getElementById("clearBtn").addEventListener("click", () => {
  points.length = 0;
  draw();
  setResult("已清空点位");
});

document.getElementById("saveBtn").addEventListener("click", async () => {
  const cameraId = Number(document.getElementById("cameraId").value);
  const roomNodeId = Number(document.getElementById("roomNodeId").value);
  if (points.length < 3) {
    setResult("至少需要3个点");
    return;
  }
  try {
    const res = await fetch(`${apiBase}/api/roi/save`, {
      method: "POST",
      credentials: "include",
      headers: {
        "Content-Type": "application/json"
      },
      body: JSON.stringify({
        cameraId,
        roomNodeId,
        verticesJson: JSON.stringify(points)
      })
    });
    const data = await res.json();
    setResult(data);
  } catch (error) {
    setResult(`保存失败：${error.message}`);
  }
});

document.getElementById("loadBtn").addEventListener("click", load);
void load();
draw();
