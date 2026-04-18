/* 文件：防区页脚本（roi.js） | File: ROI Script */
const apiBase = "";
const resultEl = document.getElementById("result");
const canvas = document.getElementById("canvas");
const ctx = canvas.getContext("2d");
const addRoiBtn = document.getElementById("addRoiBtn");
const floorCountEl = document.getElementById("floorCount");
const roiFloorListEl = document.getElementById("roiFloorList");
const roiConfigModal = document.getElementById("roiConfigModal");
const modalCameraIdInput = document.getElementById("modalCameraId");
const modalRoomNodeIdInput = document.getElementById("modalRoomNodeId");
const confirmRoiConfigBtn = document.getElementById("confirmRoiConfigBtn");
const bg = new Image();
const points = [];
let bgReady = false;
let bgLoadSeq = 0;
const staticBgPlaceholderCandidates = [];
const builtInBgPlaceholderUrl = `data:image/svg+xml;charset=UTF-8,${encodeURIComponent(
  `<svg xmlns="http://www.w3.org/2000/svg" width="1280" height="720" viewBox="0 0 1280 720">
    <defs>
      <linearGradient id="g" x1="0" y1="0" x2="1" y2="1">
        <stop offset="0%" stop-color="#1f2937"/>
        <stop offset="100%" stop-color="#0f172a"/>
      </linearGradient>
    </defs>
    <rect width="1280" height="720" fill="url(#g)"/>
    <g fill="none" stroke="#334155" stroke-width="2" opacity="0.6">
      <path d="M0 120 H1280M0 240 H1280M0 360 H1280M0 480 H1280M0 600 H1280"/>
      <path d="M160 0 V720M320 0 V720M480 0 V720M640 0 V720M800 0 V720M960 0 V720M1120 0 V720"/>
    </g>
    <g font-family="Arial, Microsoft YaHei, sans-serif" text-anchor="middle">
      <text x="640" y="340" fill="#e2e8f0" font-size="40">底图加载失败</text>
      <text x="640" y="390" fill="#94a3b8" font-size="24">已自动回退占位图</text>
    </g>
  </svg>`
)}`;
let resolvedBgPlaceholderUrl = "";
let drawArmed = false;
let latestRois = [];
let selectedFloorId = null;
let cameraFloorMap = new Map();
let floorBgPathMap = new Map();
let currentCameraId = 1;
let currentRoomNodeId = 4;

let successStatusTimer = null;
const SUCCESS_STATUS_MS = 5000;
const defaultPromptText = resultEl?.textContent ?? "";
const preferToast = Boolean(window.aura && typeof window.aura.toast === "function");

function clearSuccessStatusTimer() {
  if (successStatusTimer != null) {
    clearTimeout(successStatusTimer);
    successStatusTimer = null;
  }
}

function setResultText(message, isError) {
  const text = String(message ?? "").trim();
  if (preferToast) {
    if (text) {
      window.aura.toast(text, Boolean(isError));
    }
    return;
  }
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
    return /失败|错误|异常|超时|拒绝|未授权|无权|禁止|非法|无效|无法|不能|不存在|已过期|已失效/.test(message);
  }
  return false;
}

function setResult(data) {
  const isEmpty = !data || (typeof data === "string" && data.trim() === "");
  if (isEmpty) {
    clearSuccessStatusTimer();
    if (!preferToast) {
      setResultText(defaultPromptText, false);
    }
    return;
  }

  const message = deriveMessage(data);
  const isError = isErrorPayload(data, message);

  clearSuccessStatusTimer();
  setResultText(message, isError);

  if (!isError) {
    successStatusTimer = window.setTimeout(() => {
      successStatusTimer = null;
      if (!preferToast) {
        setResultText(defaultPromptText, false);
      }
    }, SUCCESS_STATUS_MS);
  }
}

function setDrawArmed(armed) {
  drawArmed = Boolean(armed);
  if (!addRoiBtn) return;
  addRoiBtn.classList.toggle("btn-primary", drawArmed);
  addRoiBtn.classList.toggle("btn-secondary", !drawArmed);
  addRoiBtn.textContent = drawArmed ? "保存新增防区" : "新增防区";
}

function openRoiConfigModal() {
  if (!roiConfigModal) return;
  if (modalCameraIdInput) modalCameraIdInput.value = String(currentCameraId || "");
  if (modalRoomNodeIdInput) modalRoomNodeIdInput.value = String(currentRoomNodeId || "");
  roiConfigModal.hidden = false;
}

function closeRoiConfigModal() {
  if (!roiConfigModal) return;
  roiConfigModal.hidden = true;
}

function applyRoiConfigFromModal() {
  const cameraId = Number(modalCameraIdInput?.value);
  const roomNodeId = Number(modalRoomNodeIdInput?.value);
  if (!Number.isFinite(cameraId) || cameraId <= 0) {
    setResult("请输入有效摄像头ID");
    return false;
  }
  if (!Number.isFinite(roomNodeId) || roomNodeId <= 0) {
    setResult("请输入有效房间节点ID");
    return false;
  }
  currentCameraId = cameraId;
  currentRoomNodeId = roomNodeId;
  return true;
}

async function load() {
  // 不显示“加载中...”常驻占位，避免干扰画布提示
  setResult("");

  try {
    const [roiRes, cameraRes, floorRes] = await Promise.all([
      fetch(`${apiBase}/api/roi/list`, {
        credentials: "include"
      }),
      fetch(`${apiBase}/api/camera/list`, {
        credentials: "include"
      }),
      fetch(`${apiBase}/api/floor/list`, {
        credentials: "include"
      })
    ]);
    const roiData = await roiRes.json();
    const cameraData = await cameraRes.json();
    const floorData = await floorRes.json();
    if (!roiRes.ok || roiData?.code !== 0) {
      setResult(roiData?.msg || "防区列表查询失败");
      return;
    }
    if (!cameraRes.ok || cameraData?.code !== 0) {
      setResult(cameraData?.msg || "摄像头列表查询失败");
      return;
    }
    if (!floorRes.ok || floorData?.code !== 0) {
      setResult(floorData?.msg || "楼层列表查询失败");
      return;
    }
    updateCameraFloorMap(cameraData?.data);
    updateFloorBgPathMap(floorData?.data);
    latestRois = Array.isArray(roiData?.data) ? roiData.data : [];
    syncFloorSwitchState();
  } catch (error) {
    setResult(`查询失败：${error.message}`);
  }
}

function toFiniteNumber(value) {
  const num = Number(value);
  return Number.isFinite(num) ? num : null;
}

function updateCameraFloorMap(rows) {
  cameraFloorMap = new Map();
  const list = Array.isArray(rows) ? rows : [];
  list.forEach((row) => {
    const cameraId = toFiniteNumber(row?.cameraId ?? row?.CameraId);
    const floorId = toFiniteNumber(row?.floorId ?? row?.FloorId);
    if (!Number.isFinite(cameraId) || !Number.isFinite(floorId)) return;
    cameraFloorMap.set(cameraId, floorId);
  });
}

function updateFloorBgPathMap(rows) {
  floorBgPathMap = new Map();
  const list = Array.isArray(rows) ? rows : [];
  list.forEach((row) => {
    const floorId = toFiniteNumber(row?.floorId ?? row?.FloorId);
    const filePath = String(row?.filePath ?? row?.FilePath ?? "").trim();
    if (!Number.isFinite(floorId) || !filePath) return;
    floorBgPathMap.set(floorId, filePath);
  });
}

function getFloorIdByRoi(row) {
  const cameraId = toFiniteNumber(row?.cameraId ?? row?.CameraId);
  if (!Number.isFinite(cameraId)) return null;
  return toFiniteNumber(cameraFloorMap.get(cameraId));
}

function parseVerticesFromRoi(row) {
  const raw = String(row?.verticesJson ?? row?.VerticesJson ?? "").trim();
  if (!raw) return [];
  try {
    const list = JSON.parse(raw);
    if (!Array.isArray(list)) return [];
    return list
      .map((item) => ({
        x: Number(item?.x),
        y: Number(item?.y)
      }))
      .filter((item) => Number.isFinite(item.x) && Number.isFinite(item.y))
      .map((item) => ({
        x: Number(item.x.toFixed(2)),
        y: Number(item.y.toFixed(2))
      }));
  } catch {
    return [];
  }
}

function applyRoiToEditor(row) {
  if (!row) return;
  const cameraId = toFiniteNumber(row?.cameraId ?? row?.CameraId);
  const roomNodeId = toFiniteNumber(row?.roomNodeId ?? row?.RoomNodeId);
  if (Number.isFinite(cameraId)) {
    currentCameraId = cameraId;
  }
  if (Number.isFinite(roomNodeId)) {
    currentRoomNodeId = roomNodeId;
  }
  const parsedPoints = parseVerticesFromRoi(row);
  points.length = 0;
  points.push(...parsedPoints);
  draw();
}

function probeImageLoadable(url) {
  return new Promise((resolve) => {
    const probe = new Image();
    probe.onload = () => resolve(true);
    probe.onerror = () => resolve(false);
    probe.src = url;
  });
}

function getDynamicBgPlaceholderCandidates() {
  const values = Array.from(floorBgPathMap.values());
  return values
    .map((filePath) => normalizeFloorImagePathToUrl(filePath, apiBase))
    .filter(Boolean);
}

async function resolveBackgroundPlaceholderUrl(extraCandidates = []) {
  const dynamicCandidates = getDynamicBgPlaceholderCandidates();
  const candidates = [...extraCandidates, ...dynamicCandidates, ...staticBgPlaceholderCandidates];
  for (const candidate of candidates) {
    if (resolvedBgPlaceholderUrl && candidate === resolvedBgPlaceholderUrl) {
      return resolvedBgPlaceholderUrl;
    }
    const ok = await probeImageLoadable(candidate);
    if (ok) {
      resolvedBgPlaceholderUrl = candidate;
      return resolvedBgPlaceholderUrl;
    }
  }
  resolvedBgPlaceholderUrl = builtInBgPlaceholderUrl;
  return resolvedBgPlaceholderUrl;
}

function loadBackgroundFallback(loadSeq, failedUrl = "") {
  const preferredCandidates = [];
  if (failedUrl) {
    preferredCandidates.push(failedUrl);
  }
  void resolveBackgroundPlaceholderUrl(preferredCandidates).then((fallbackUrl) => {
    if (loadSeq !== bgLoadSeq) return;
    bg.onload = () => {
      if (loadSeq !== bgLoadSeq) return;
      bgReady = true;
      draw();
      setResult("底图加载失败，已自动回退占位图");
    };
    bg.onerror = () => {
      if (loadSeq !== bgLoadSeq) return;
      bgReady = false;
      draw();
      setResult("底图加载失败，且占位图加载失败");
    };
    bg.src = fallbackUrl;
  });
}

function loadBackgroundByUrl(url, showMessage = true) {
  const text = normalizeFloorImagePathToUrl(url, apiBase);
  const loadSeq = ++bgLoadSeq;
  if (!text) {
    loadBackgroundFallback(loadSeq, text);
    return;
  }
  bgReady = false;
  bg.onload = () => {
    if (loadSeq !== bgLoadSeq) return;
    bgReady = true;
    draw();
    if (showMessage) setResult("底图加载成功");
  };
  bg.onerror = () => {
    if (loadSeq !== bgLoadSeq) return;
    loadBackgroundFallback(loadSeq);
  };
  bg.src = text;
}

function selectFloorById(floorId) {
  selectedFloorId = floorId;
  setDrawArmed(false);
  const floorRois = latestRois.filter((row) => Number(getFloorIdByRoi(row)) === Number(floorId));
  if (floorRois.length > 0) {
    applyRoiToEditor(floorRois[0]);
  } else {
    points.length = 0;
    draw();
  }
  const bgPath = String(floorBgPathMap.get(Number(floorId)) ?? "");
  loadBackgroundByUrl(bgPath, false);
  renderFloorSwitchList();
}

function renderFloorSwitchList() {
  if (!roiFloorListEl) return;
  const floorCounts = new Map();
  latestRois.forEach((row) => {
    const floorId = getFloorIdByRoi(row);
    if (!Number.isFinite(floorId)) return;
    floorCounts.set(floorId, (floorCounts.get(floorId) || 0) + 1);
  });
  const floorIds = Array.from(floorCounts.keys()).sort((a, b) => b - a);
  if (floorCountEl) {
    floorCountEl.textContent = floorIds.length > 0 ? `共 ${floorIds.length} 层` : "暂无楼层";
  }
  roiFloorListEl.innerHTML = "";
  if (floorIds.length === 0) {
    const emptyEl = document.createElement("div");
    emptyEl.className = "roi-floor-item";
    emptyEl.textContent = "暂无可切换楼层";
    roiFloorListEl.appendChild(emptyEl);
    return;
  }
  floorIds.forEach((floorId) => {
    const btn = document.createElement("button");
    btn.type = "button";
    btn.className = `roi-floor-item${Number(selectedFloorId) === Number(floorId) ? " is-active" : ""}`;
    const nameEl = document.createElement("span");
    nameEl.className = "roi-floor-name";
    nameEl.textContent = `${floorId}层`;
    const badgeEl = document.createElement("span");
    badgeEl.className = "roi-floor-badge";
    badgeEl.textContent = `${floorCounts.get(floorId) || 0} 个防区`;
    btn.appendChild(nameEl);
    btn.appendChild(badgeEl);
    btn.addEventListener("click", () => {
      selectFloorById(floorId);
      setResult(`已切换到 ${floorId} 层`);
    });
    roiFloorListEl.appendChild(btn);
  });
}

function syncFloorSwitchState() {
  if (!latestRois.length) {
    selectedFloorId = null;
    points.length = 0;
    draw();
    renderFloorSwitchList();
    setResult("当前暂无防区数据");
    return;
  }
  const firstMatchFloorId = getFloorIdByRoi(latestRois[0]);
  if (!Number.isFinite(selectedFloorId)) {
    selectedFloorId = Number.isFinite(firstMatchFloorId) ? firstMatchFloorId : null;
  }
  if (!Number.isFinite(selectedFloorId)) {
    points.length = 0;
    draw();
    renderFloorSwitchList();
    setResult("防区数据未关联可切换楼层，请先检查摄像头楼层配置");
    return;
  }
  selectFloorById(selectedFloorId);
  const floorTotal = latestRois.filter((row) => Number(getFloorIdByRoi(row)) === Number(selectedFloorId)).length;
  setResult(`已加载 ${selectedFloorId} 层防区，共 ${floorTotal} 条`);
}

async function saveCurrentRoi() {
  const cameraId = Number(currentCameraId);
  const roomNodeId = Number(currentRoomNodeId);
  if (!Number.isFinite(cameraId) || cameraId <= 0 || !Number.isFinite(roomNodeId) || roomNodeId <= 0) {
    setResult("请先配置摄像头ID与房间节点ID");
    openRoiConfigModal();
    return;
  }
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
    if (res.ok && data?.code === 0) {
      setDrawArmed(false);
      await load();
    }
  } catch (error) {
    setResult(`保存失败：${error.message}`);
  }
}

function draw() {
  ctx.clearRect(0, 0, canvas.width, canvas.height);
  const bgDrawable = bgReady && bg.complete && bg.naturalWidth > 0 && bg.naturalHeight > 0;
  if (bgDrawable) {
    ctx.drawImage(bg, 0, 0, canvas.width, canvas.height);
  } else {
    bgReady = false;
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
  if (!drawArmed) {
    return;
  }
  const pos = getPos(e);
  points.push({ x: Number(pos.x.toFixed(2)), y: Number(pos.y.toFixed(2)) });
  draw();
  setResult({ points });
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

addRoiBtn?.addEventListener("click", () => {
  if (drawArmed) {
    void saveCurrentRoi();
    return;
  }
  openRoiConfigModal();
});

confirmRoiConfigBtn?.addEventListener("click", () => {
  if (!applyRoiConfigFromModal()) return;
  closeRoiConfigModal();
  points.length = 0;
  draw();
  setDrawArmed(true);
  setResult(`已开启新增防区：摄像头ID=${currentCameraId}，房间节点ID=${currentRoomNodeId}。请在图纸上落点后再次点击“保存新增防区”。`);
});

document.querySelectorAll("[data-roi-modal-dismiss]").forEach((el) => {
  el.addEventListener("click", () => closeRoiConfigModal());
});
if (roiConfigModal) {
  roiConfigModal.addEventListener("click", (e) => {
    if (e.target instanceof HTMLElement && e.target.classList.contains("aura-modal-backdrop")) {
      closeRoiConfigModal();
    }
  });
}
window.addEventListener("keydown", (e) => {
  if (e.key === "Escape" && roiConfigModal && !roiConfigModal.hidden) {
    closeRoiConfigModal();
  }
});

document.getElementById("loadBtn").addEventListener("click", load);
void load();
draw();
setDrawArmed(false);
if (preferToast && resultEl) {
  resultEl.hidden = true;
}
