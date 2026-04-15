/* 文件：轨迹页脚本（track.js） | File: Track Script */
const apiBase = "";
const resultEl = document.getElementById("result");
const canvas = document.getElementById("canvas");
const ctx = canvas.getContext("2d");
/** 与后端摄像机平面坐标一致的逻辑画布尺寸（绘制在此坐标系内进行） */
const TRACK_REF_WIDTH = 900;
const TRACK_REF_HEIGHT = 520;
let route = [];
let timer = null;
let step = 0;
let lastBitmapW = 0;
let lastBitmapH = 0;

function readCssColor(varName, fallback) {
  const raw = getComputedStyle(document.documentElement).getPropertyValue(varName).trim();
  return raw || fallback;
}

function resizeCanvasBitmapIfNeeded() {
  const rect = canvas.getBoundingClientRect();
  const cssW = Math.max(1, Math.round(rect.width));
  const cssH = Math.max(1, Math.round(rect.height));
  const dpr = window.devicePixelRatio || 1;
  const bw = Math.round(cssW * dpr);
  const bh = Math.round(cssH * dpr);
  if (bw !== lastBitmapW || bh !== lastBitmapH) {
    canvas.width = bw;
    canvas.height = bh;
    lastBitmapW = bw;
    lastBitmapH = bh;
  }
}

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
    return /失败|错误|异常|超时|拒绝|未授权|无权|禁止|非法|无效|无法|不能|不存在|已过期|已失效/.test(message);
  }
  return false;
}

/**
 * 提示：仅用居中 Toast，不写页面内状态框。
 * @param {string} message
 * @param {boolean} [isError=true] 校验失败等为 true；无轨迹数据、需先查询等为 false（非错误态）
 */
function showFieldHint(message, isError = true) {
  const text = String(message || "").trim();
  if (!text) return;
  if (window.aura && typeof window.aura.toast === "function") {
    window.aura.toast(text, isError);
    return;
  }
  setResult(text);
}

/**
 * 兼容 /api/track/{vid} 两种返回：data 为事件数组，或 data.points（旧/监控封装）。
 * @param {object} trackPayload 接口 JSON
 * @returns {unknown[]}
 */
function normalizeTrackEvents(trackPayload) {
  const d = trackPayload?.data;
  if (Array.isArray(d)) return d;
  if (d && typeof d === "object" && Array.isArray(d.points)) return d.points;
  return [];
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

async function load(options = {}) {
  const vid = document.getElementById("vid").value.trim();
  setResult("");

  if (!vid) {
    showFieldHint("请输入人员虚拟编号");
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

    if (!cameraRes.ok || cameraData.code !== 0) {
      setResult(cameraData?.msg ? cameraData : { code: cameraRes.status, msg: `摄像机列表加载失败：HTTP ${cameraRes.status}` });
      return;
    }

    if (!trackRes.ok || trackData.code !== 0) {
      setResult(trackData?.msg ? trackData : { code: trackRes.status, msg: `轨迹查询失败：HTTP ${trackRes.status}` });
      return;
    }

    const camList = Array.isArray(cameraData?.data) ? cameraData.data : [];
    const camMap = new Map(camList.map((x) => [x.cameraId ?? x.CameraId, x]));

    const rawEvents = normalizeTrackEvents(trackData);
    const mapped = rawEvents
      .map((p) => {
        const cameraId = p?.cameraId ?? p?.CameraId;
        const roiId = p?.roiId ?? p?.RoiId;
        const time = p?.time ?? p?.eventTime ?? p?.EventTime;
        const c = camMap.get(cameraId);
        if (!c) return null;
        return {
          x: Number(c.posX ?? c.PosX),
          y: Number(c.posY ?? c.PosY),
          cameraId,
          roiId,
          time
        };
      })
      .filter(Boolean);
    route = mapped.reverse();
    step = 0;
    drawRoute();

    if (route.length === 0) {
      if (rawEvents.length === 0) {
        showFieldHint("暂无该人员的轨迹事件数据。以图搜轨返回的 VID 若来自向量测试数据，可能尚未写入轨迹库。", false);
      } else {
        showFieldHint("已返回轨迹事件，但摄像机未配置平面坐标或摄像机 ID 不匹配，无法在画布上绘制。请到摄像头页检查对应设备的点位。", false);
      }
      return;
    }

    if (!options.silentSuccessToast) {
      setResult(`轨迹加载成功：共 ${route.length} 个点。`);
    }
  } catch (error) {
    setResult(`查询失败：${error.message}`);
  }
}

function drawRoute() {
  resizeCanvasBitmapIfNeeded();
  const wPx = canvas.width;
  const hPx = canvas.height;
  ctx.setTransform(1, 0, 0, 1, 0, 0);
  ctx.clearRect(0, 0, wPx, hPx);
  ctx.fillStyle = readCssColor("--canvas-bg", "#0f1724");
  ctx.fillRect(0, 0, wPx, hPx);

  const scaleX = wPx / TRACK_REF_WIDTH;
  const scaleY = hPx / TRACK_REF_HEIGHT;
  ctx.setTransform(scaleX, 0, 0, scaleY, 0, 0);

  const lineColor = readCssColor("--primary", "#2563eb");
  const pointActive = readCssColor("--danger", "rgb(239, 68, 68)");
  const pointInactive = readCssColor("--text-muted", "#64748b");

  if (route.length > 0) {
    ctx.strokeStyle = lineColor;
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
    ctx.fillStyle = i <= step ? pointActive : pointInactive;
    ctx.beginPath();
    ctx.arc(p.x, p.y, i === step ? 7 : 4, 0, Math.PI * 2);
    ctx.fill();
  }
}

function play() {
  if (route.length === 0) {
    showFieldHint("请先查询轨迹", false);
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
document.getElementById("load").addEventListener("click", () => load());

function applyVidFromQuery() {
  try {
    const params = new URLSearchParams(window.location.search);
    const vid = String(params.get("vid") || "").trim();
    const vidEl = document.getElementById("vid");
    if (!vid || !(vidEl instanceof HTMLInputElement)) return;
    vidEl.value = vid;
    void load({ silentSuccessToast: true });
  } catch {
    /* 查询参数解析失败时忽略 */
  }
}
applyVidFromQuery();

let resizeTimer = null;
function scheduleRedraw() {
  if (resizeTimer != null) window.clearTimeout(resizeTimer);
  resizeTimer = window.setTimeout(() => {
    resizeTimer = null;
    drawRoute();
  }, 80);
}
window.addEventListener("resize", scheduleRedraw);
if (typeof ResizeObserver !== "undefined" && canvas.parentElement) {
  new ResizeObserver(scheduleRedraw).observe(canvas.parentElement);
}

requestAnimationFrame(() => {
  drawRoute();
});
