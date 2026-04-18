/* 文件：摄像头页脚本（camera.js） | File: Camera Script */
const apiBase = "";
const canvas = document.getElementById("canvas");
const ctx = canvas.getContext("2d");
const resultEl = document.getElementById("result");
const floorSwitchListEl = document.getElementById("floorSwitchList");
const floorCountEl = document.getElementById("floorCount");
const pointModal = document.getElementById("cameraPointModal");
const modalFloorIdInput = document.getElementById("modalFloorId");
const modalDeviceIdInput = document.getElementById("modalDeviceId");
const modalChannelNoInput = document.getElementById("modalChannelNo");
const modalPosXInput = document.getElementById("modalPosX");
const modalPosYInput = document.getElementById("modalPosY");
const confirmAddPointBtn = document.getElementById("confirmAddPointBtn");
const addPointBtn = document.getElementById("addPointBtn");
const bg = new Image();
const points = [];
let dragIndex = -1;
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
let pendingPointPos = null;
let selectedFloorId = null;
let selectedFloorBgPath = "";
let addPointArmed = false;
let lastDeviceId = 1;
let lastChannelNo = 1;
let floorBgPathMap = new Map();
const preferToast = Boolean(window.aura && typeof window.aura.toast === "function");

function setResultText(message, isError = false) {
  if (!resultEl) return;
  resultEl.textContent = String(message ?? "");
  resultEl.hidden = false;
  resultEl.classList.toggle("is-error", Boolean(isError));
}

function showToast(message, isError = false) {
  const text = String(message ?? "").trim();
  if (!text) return;
  if (preferToast) {
    window.aura.toast(text, isError);
    return;
  }
  setResultText(text, isError);
}

function isErrorText(text) {
  return /失败|错误|异常|无权限|超时|断开|拒绝|未授权|无权|禁止|非法|无效|无法|不能|不存在|已过期|已失效/.test(String(text ?? ""));
}

function setResult(data) {
  if (typeof data === "string") {
    showToast(data, isErrorText(data));
    return;
  }
  if (data && typeof data === "object") {
    if (typeof data.msg === "string") {
      showToast(data.msg, isErrorText(data.msg));
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
  showToast(text, isErrorText(text));
}

function setAddPointArmed(armed) {
  addPointArmed = Boolean(armed);
  if (!addPointBtn) return;
  addPointBtn.classList.toggle("btn-primary", addPointArmed);
  addPointBtn.classList.toggle("btn-secondary", !addPointArmed);
  addPointBtn.textContent = addPointArmed ? "新增点位（点击地图）" : "新增点位";
}

function parseOptionalNumberInput(el) {
  const raw = String(el?.value ?? "").trim();
  if (!raw) return null;
  const value = Number(raw);
  return Number.isFinite(value) ? value : null;
}

function openPointModal(pos) {
  if (!pointModal) return;
  pendingPointPos = { x: Number(pos.x), y: Number(pos.y) };
  if (modalFloorIdInput) {
    modalFloorIdInput.value = Number.isFinite(selectedFloorId) ? String(selectedFloorId) : "";
  }
  if (modalDeviceIdInput) modalDeviceIdInput.value = String(lastDeviceId);
  if (modalChannelNoInput) modalChannelNoInput.value = String(lastChannelNo);
  if (modalPosXInput) modalPosXInput.value = pendingPointPos.x.toFixed(2);
  if (modalPosYInput) modalPosYInput.value = pendingPointPos.y.toFixed(2);
  pointModal.hidden = false;
}

function closePointModal() {
  if (!pointModal) return;
  pointModal.hidden = true;
  pendingPointPos = null;
}

function getFloorBgPath(rows, floorId) {
  if (!Number.isFinite(floorId)) return "";
  const matched = rows.find((row) => Number(row?.floorId ?? row?.FloorId) === floorId);
  return String(matched?.filePath ?? matched?.FilePath ?? "").trim();
}

async function loadFloorBgMap() {
  const res = await fetch(`${apiBase}/api/floor/list`, {
    credentials: "include"
  });
  const data = await res.json();
  if (!res.ok || data?.code !== 0) {
    return new Map();
  }
  const rows = Array.isArray(data?.data) ? data.data : [];
  const map = new Map();
  rows.forEach((row) => {
    const floorId = Number(row?.floorId ?? row?.FloorId);
    const filePath = String(row?.filePath ?? row?.FilePath ?? "").trim();
    if (!Number.isFinite(floorId) || !filePath) return;
    map.set(floorId, filePath);
  });
  floorBgPathMap = map;
  return map;
}

function renderFloorSwitchList(floorIds, floorCounts, activeFloorId) {
  if (!floorSwitchListEl) return;
  if (floorCountEl) {
    floorCountEl.textContent = floorIds.length > 0 ? `共 ${floorIds.length} 层` : "暂无楼层";
  }
  floorSwitchListEl.innerHTML = "";
  if (floorIds.length === 0) {
    const empty = document.createElement("div");
    empty.className = "camera-floor-item";
    empty.textContent = "暂无点位楼层";
    floorSwitchListEl.appendChild(empty);
    return;
  }
  floorIds.forEach((floorId) => {
    const btn = document.createElement("button");
    btn.type = "button";
    btn.className = `camera-floor-item${Number(activeFloorId) === Number(floorId) ? " is-active" : ""}`;
    const count = floorCounts.get(floorId) || 0;
    const nameEl = document.createElement("span");
    nameEl.className = "camera-floor-name";
    nameEl.textContent = `${floorId}层`;
    const badgeEl = document.createElement("span");
    badgeEl.className = "camera-floor-badge";
    badgeEl.textContent = `${count} 点`;
    btn.appendChild(nameEl);
    btn.appendChild(badgeEl);
    btn.addEventListener("click", () => {
      selectedFloorId = floorId;
      loadPoints();
    });
    floorSwitchListEl.appendChild(btn);
  });
}

function pickMostPopulatedFloor(rows) {
  const counts = new Map();
  rows.forEach((row) => {
    const floorId = Number(row?.floorId ?? row?.FloorId);
    if (!Number.isFinite(floorId)) return;
    counts.set(floorId, (counts.get(floorId) || 0) + 1);
  });
  let bestFloorId = null;
  let bestCount = -1;
  counts.forEach((count, floorId) => {
    if (count > bestCount || (count === bestCount && (bestFloorId === null || floorId < bestFloorId))) {
      bestFloorId = floorId;
      bestCount = count;
    }
  });
  return bestFloorId;
}

function getDynamicBgPlaceholderCandidates() {
  return Array.from(floorBgPathMap.values())
    .map((filePath) => normalizeFloorImagePathToUrl(filePath, apiBase))
    .filter(Boolean);
}

function probeImageLoadable(url) {
  return new Promise((resolve) => {
    const probe = new Image();
    probe.onload = () => resolve(true);
    probe.onerror = () => resolve(false);
    probe.src = url;
  });
}

async function resolveBackgroundPlaceholderUrl(extraCandidates = []) {
  const candidates = [...extraCandidates, ...getDynamicBgPlaceholderCandidates(), ...staticBgPlaceholderCandidates];
  for (const candidate of candidates) {
    if (!candidate) continue;
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
  const preferred = failedUrl ? [failedUrl] : [];
  void resolveBackgroundPlaceholderUrl(preferred).then((fallbackUrl) => {
    if (loadSeq !== bgLoadSeq) return;
    bg.onload = () => {
      if (loadSeq !== bgLoadSeq) return;
      bgReady = true;
      draw();
      showToast("底图加载失败，已自动回退占位图", true);
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

function loadBackgroundByUrl(url) {
  const text = normalizeFloorImagePathToUrl(url, apiBase);
  const loadSeq = ++bgLoadSeq;
  if (!text) {
    loadBackgroundFallback(loadSeq, text);
    return;
  }
  bg.onload = () => {
    if (loadSeq !== bgLoadSeq) return;
    bgReady = true;
    draw();
    showToast("底图加载成功");
  };
  bg.onerror = () => {
    if (loadSeq !== bgLoadSeq) return;
    loadBackgroundFallback(loadSeq, text);
  };
  bg.src = text;
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
    const tag = p.cameraId ? `#${p.cameraId}` : `#${i + 1}`;
    ctx.fillText(tag, p.x + 8, p.y - 8);
  });
}

function getPos(e) {
  const rect = canvas.getBoundingClientRect();
  return { x: e.clientX - rect.left, y: e.clientY - rect.top };
}

canvas.addEventListener("mousedown", (e) => {
  const pos = getPos(e);
  dragIndex = points.findIndex((p) => Math.hypot(p.x - pos.x, p.y - pos.y) < 10);
  if (dragIndex >= 0) {
    const p = points[dragIndex];
    // 目前后端仅支持 create，不支持 update；禁止拖拽已保存点位，避免误以为“修改成功”但实际重复创建
    if (p && p.saved) {
      dragIndex = -1;
      setResult("已保存点位不支持拖拽修改。如需调整，请删除后重新创建。");
      return;
    }
    return;
  }
  if (dragIndex < 0) {
    if (!addPointArmed) {
      return;
    }
    setAddPointArmed(false);
    openPointModal(pos);
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

if (addPointBtn) {
  addPointBtn.addEventListener("click", () => {
    const nextState = !addPointArmed;
    setAddPointArmed(nextState);
    if (nextState) {
      showToast("已开启新增点位：请点击地图选择坐标");
    } else {
      showToast("已取消新增点位");
    }
  });
}

if (confirmAddPointBtn) {
  confirmAddPointBtn.addEventListener("click", async () => {
    if (!pendingPointPos) {
      setResult("未获取到待新增点位坐标，请重试。");
      closePointModal();
      return;
    }
    const floorId = parseOptionalNumberInput(modalFloorIdInput);
    const deviceId = Number(modalDeviceIdInput?.value);
    const channelNo = Number(modalChannelNoInput?.value);
    if (!Number.isFinite(floorId)) {
      setResult("请输入有效楼层ID。");
      return;
    }
    if (!Number.isFinite(deviceId) || deviceId <= 0) {
      setResult("请输入有效设备ID。");
      return;
    }
    if (!Number.isFinite(channelNo) || channelNo <= 0) {
      setResult("请输入有效通道号。");
      return;
    }
    const posX = Number(pendingPointPos.x.toFixed(2));
    const posY = Number(pendingPointPos.y.toFixed(2));
    lastDeviceId = deviceId;
    lastChannelNo = channelNo;
    selectedFloorId = floorId;
    try {
      const res = await fetch(`${apiBase}/api/camera/create`, {
        method: "POST",
        credentials: "include",
        headers: {
          "Content-Type": "application/json"
        },
        body: JSON.stringify({
          floorId,
          deviceId,
          channelNo,
          posX,
          posY
        })
      });
      const data = await res.json();
      if (!res.ok || data?.code !== 0) {
        setResult(data?.msg || "新增点位失败");
        return;
      }
      closePointModal();
      showToast("新增点位成功");
      await loadPoints();
    } catch (error) {
      setResult(`新增点位失败：${error.message}`);
    }
  });
}

async function loadPoints() {
  let floorId = Number.isFinite(selectedFloorId) ? Number(selectedFloorId) : null;
  try {
    const [cameraRes, floorBgMap] = await Promise.all([
      fetch(`${apiBase}/api/camera/list`, {
        credentials: "include"
      }),
      loadFloorBgMap()
    ]);
    const data = await cameraRes.json();
    if (!cameraRes.ok || data?.code !== 0) {
      setResult(data?.msg || "查询失败");
      return;
    }
    const rows = Array.isArray(data?.data) ? data.data : [];
    const floorCounts = new Map();
    rows.forEach((x) => {
      const id = Number(x?.floorId ?? x?.FloorId);
      if (!Number.isFinite(id)) return;
      floorCounts.set(id, (floorCounts.get(id) || 0) + 1);
    });
    const floorIds = Array.from(
      new Set(
        rows
          .map((x) => Number(x?.floorId ?? x?.FloorId))
          .filter((x) => Number.isFinite(x))
      )
    ).sort((a, b) => b - a);
    if (!Number.isFinite(floorId) && floorIds.length > 0) {
      const autoFloor = pickMostPopulatedFloor(rows);
      if (Number.isFinite(autoFloor)) {
        floorId = Number(autoFloor);
        selectedFloorId = floorId;
      }
    }
    selectedFloorBgPath = String(floorBgMap.get(Number(floorId)) ?? "");
    if (!selectedFloorBgPath) {
      selectedFloorBgPath = getFloorBgPath(rows, floorId);
    }
    renderFloorSwitchList(floorIds, floorCounts, floorId);
    const filtered = Number.isFinite(floorId) ? rows.filter((x) => Number(x?.floorId ?? x?.FloorId) === floorId) : rows;
    points.length = 0;
    filtered.forEach((row) => {
      const x = Number(row?.posX ?? row?.PosX);
      const y = Number(row?.posY ?? row?.PosY);
      if (!Number.isFinite(x) || !Number.isFinite(y)) return;
      points.push({
        cameraId: Number(row?.cameraId ?? row?.CameraId) || null,
        floorId: Number(row?.floorId ?? row?.FloorId) || floorId,
        deviceId: Number(row?.deviceId ?? row?.DeviceId) || null,
        channelNo: Number(row?.channelNo ?? row?.ChannelNo) || null,
        x,
        y,
        saved: true
      });
    });
    draw();
    if (selectedFloorBgPath) {
      loadBackgroundByUrl(selectedFloorBgPath);
    } else {
      bgReady = false;
      draw();
    }
    if (points.length > 0) {
      showToast(`点位已刷新：${points.length} 条`);
      return;
    }
    if (Number.isFinite(floorId)) {
      if (floorIds.length === 0) {
        setResult("当前系统暂无摄像头点位数据。");
        return;
      }
      const recommend = floorIds.join("、");
      setResult(`楼层 ${floorId} 暂无点位。可用楼层：${recommend}`);
      return;
    }
    if (floorIds.length === 0) {
      setResult("当前系统暂无摄像头点位数据。");
      return;
    }
    setResult(`当前共 ${rows.length} 条点位，覆盖楼层：${floorIds.join("、")}`);
  } catch (error) {
    setResult(`查询失败：${error.message}`);
  }
}

document.getElementById("listBtn").addEventListener("click", loadPoints);
document.querySelectorAll("[data-camera-modal-dismiss]").forEach((el) => {
  el.addEventListener("click", () => closePointModal());
});
if (pointModal) {
  pointModal.addEventListener("click", (e) => {
    if (e.target instanceof HTMLElement && e.target.classList.contains("aura-modal-backdrop")) {
      closePointModal();
    }
  });
}
window.addEventListener("keydown", (e) => {
  if (e.key === "Escape" && pointModal && !pointModal.hidden) {
    closePointModal();
  }
});
draw();
setAddPointArmed(false);
loadPoints();
if (preferToast && resultEl) {
  resultEl.hidden = true;
}
