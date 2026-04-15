/* 文件：三维态势页脚本（scene.js） | File: Scene Script */
const apiBase = "";
const resultEl = document.getElementById("result");
const stage = document.getElementById("stage3d");
const slice2d = document.getElementById("slice2d");
const eventFeedEl = document.getElementById("eventFeed");
const eventFilterBarEl = document.getElementById("eventFilterBar");
const metricFloorsEl = document.getElementById("metricFloors");
const metricCamerasEl = document.getElementById("metricCameras");
const metricAlertsEl = document.getElementById("metricAlerts");
const metricModeEl = document.getElementById("metricMode");
const floorSummaryEl = document.getElementById("floorSummary");
const floorChipsEl = document.getElementById("floorChips");
const sctx = slice2d.getContext("2d");
const floorMeshes = [];
let floorData = [];
let cameraData = [];
let currentFloorId = null;
const EVENT_FEED_LIMIT = 12;
const eventFeedItems = [];
let currentEventFilter = "all";

function showToast(message, isError = false) {
  const text = String(message ?? "").trim();
  if (!text) return;
  if (window.aura && typeof window.aura.toast === "function") {
    window.aura.toast(text, isError);
    return;
  }
}

function isErrorText(text) {
  return /失败|错误|异常|未|无权限|超时|断开/.test(String(text ?? ""));
}

const scene = new THREE.Scene();
scene.background = new THREE.Color(0x101826);
const camera = new THREE.PerspectiveCamera(60, 1, 0.1, 1000);
camera.position.set(28, 24, 28);
camera.lookAt(0, 6, 0);
const renderer = new THREE.WebGLRenderer({ antialias: true });
const raycaster = new THREE.Raycaster();
const mouse = new THREE.Vector2();
const FLOOR_SELECTED_EMISSIVE = 0x2f8cff;
const FLOOR_FLASH_EMISSIVE = 0xff3b3b;
const FLOOR_DEFAULT_EMISSIVE = 0x000000;

function applySelectedFloorHighlight() {
  for (const mesh of floorMeshes) {
    const isSelected = currentFloorId != null && Number(mesh.userData.floorId) === Number(currentFloorId);
    mesh.material.emissive.setHex(isSelected ? FLOOR_SELECTED_EMISSIVE : FLOOR_DEFAULT_EMISSIVE);
  }
}

function focusCameraToFloorStack() {
  if (!floorMeshes.length) {
    camera.position.set(28, 24, 28);
    camera.lookAt(0, 6, 0);
    return;
  }

  const box = new THREE.Box3();
  for (const m of floorMeshes) box.expandByObject(m);
  const center = new THREE.Vector3();
  const size = new THREE.Vector3();
  box.getCenter(center);
  box.getSize(size);

  const maxDim = Math.max(size.x, size.y, size.z, 1);
  const fov = (camera.fov * Math.PI) / 180;
  const fitHeightDist = (maxDim / 2) / Math.tan(fov / 2);
  const fitWidthDist = fitHeightDist / Math.max(0.8, camera.aspect || 1);
  const dist = Math.max(fitHeightDist, fitWidthDist) * 1.2 + 6;

  camera.position.set(center.x + dist, center.y + dist * 0.65, center.z + dist);
  camera.lookAt(center);
}

function setResult(data) {
  if (!resultEl) return;
  if (typeof data === "string") {
    if (isErrorText(data)) {
      resultEl.textContent = data;
      resultEl.classList.add("is-error");
    } else {
      showToast(data, false);
      resultEl.classList.remove("is-error");
    }
    return;
  }
  if (data && typeof data === "object") {
    if (typeof data.msg === "string") {
      if (isErrorText(data.msg)) {
        resultEl.textContent = data.msg;
        resultEl.classList.add("is-error");
      } else {
        showToast(data.msg, false);
        resultEl.classList.remove("is-error");
      }
      return;
    }
    if (Array.isArray(data.data)) {
      showToast(`共 ${data.data.length} 条结果`, false);
      resultEl.classList.remove("is-error");
      return;
    }
    resultEl.classList.remove("is-error");
    return;
  }
  const text = String(data ?? "");
  if (isErrorText(text)) {
    resultEl.textContent = text;
    resultEl.classList.add("is-error");
    return;
  }
  showToast(text, false);
  resultEl.classList.remove("is-error");
}

function toDisplayTime(value) {
  if (typeof window.formatDateTimeDisplay === "function") {
    return window.formatDateTimeDisplay(value, "-");
  }
  const d = new Date(value);
  if (Number.isNaN(d.getTime())) return String(value ?? "-");
  return d.toLocaleString("zh-CN", { hour12: false });
}

function escapeHtml(value) {
  return String(value ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#39;");
}

function normalizeEventItem(item) {
  const type = String(item?.eventType || item?.type || "event").toLowerCase();
  const timeRaw = item?.eventTime || item?.captureTime || item?.createdAt || item?.at || new Date().toISOString();
  const timeValue = new Date(timeRaw).getTime();
  return {
    type,
    title: String(item?.title || item?.alertType || (type === "capture" ? "抓拍事件" : type === "alert" ? "告警事件" : "态势事件")),
    detail: String(item?.detail || item?.msg || item?.metadata || ""),
    cameraId: item?.cameraId ? Number(item.cameraId) : null,
    floorId: item?.floorId ? Number(item.floorId) : null,
    eventTime: Number.isNaN(timeValue) ? Date.now() : timeValue,
    eventTimeText: toDisplayTime(timeRaw)
  };
}

function renderEventFeed() {
  if (!eventFeedEl) return;
  const filteredItems =
    currentEventFilter === "all" ? eventFeedItems : eventFeedItems.filter((x) => x.type === currentEventFilter);
  if (!filteredItems.length) {
    eventFeedEl.innerHTML = `<li><strong>暂无事件</strong><span>等待实时事件或刷新历史加载。</span></li>`;
    return;
  }
  eventFeedEl.innerHTML = filteredItems
    .map(
      (item) => `<li>
      <strong>${escapeHtml(item.title)} · ${escapeHtml(item.eventTimeText)}</strong>
      <span>${escapeHtml(item.detail || "无详情")}</span>
    </li>`
    )
    .join("");
}

function pushEventFeed(item) {
  const normalized = normalizeEventItem(item);
  eventFeedItems.unshift(normalized);
  if (eventFeedItems.length > EVENT_FEED_LIMIT) {
    eventFeedItems.length = EVENT_FEED_LIMIT;
  }
  renderEventFeed();
}

function bindEventFilter() {
  if (!eventFilterBarEl) return;
  eventFilterBarEl.querySelectorAll("[data-event-filter]").forEach((btn) => {
    btn.addEventListener("click", () => {
      const filter = String(btn.getAttribute("data-event-filter") || "all").toLowerCase();
      currentEventFilter = filter;
      eventFilterBarEl.querySelectorAll("[data-event-filter]").forEach((x) => {
        x.classList.toggle("is-active", x === btn);
      });
      renderEventFeed();
    });
  });
}

function setMetrics() {
  if (metricFloorsEl) metricFloorsEl.textContent = String(floorData.length);
  if (metricCamerasEl) metricCamerasEl.textContent = String(cameraData.length);
  if (metricAlertsEl) {
    const alertCount = eventFeedItems.filter((x) => x.type === "alert").length;
    metricAlertsEl.textContent = String(alertCount);
  }
  if (metricModeEl) metricModeEl.textContent = currentFloorId ? "2D切片" : "3D总览";
  if (floorSummaryEl) {
    if (!currentFloorId) {
      floorSummaryEl.textContent = `当前未选中楼层，展示全局态势。楼层 ${floorData.length} 个，摄像头 ${cameraData.length} 个。`;
    } else {
      const cams = cameraData.filter((c) => Number(c.floorId) === Number(currentFloorId));
      floorSummaryEl.textContent = `楼层 #${currentFloorId} 已选中，当前楼层摄像头 ${cams.length} 个。`;
    }
  }
}

function renderFloorChips() {
  if (!floorChipsEl) return;
  if (!floorData.length) {
    floorChipsEl.innerHTML = "";
    return;
  }
  floorChipsEl.innerHTML = floorData
    .map((f) => {
      const floorId = Number(f.floorId ?? f.FloorId);
      const active = currentFloorId && Number(currentFloorId) === floorId ? " is-active" : "";
      return `<button type="button" class="floor-chip${active}" data-floor-id="${floorId}">F${floorId}</button>`;
    })
    .join("");
  floorChipsEl.querySelectorAll("[data-floor-id]").forEach((btn) => {
    btn.addEventListener("click", () => {
      const floorId = Number(btn.getAttribute("data-floor-id"));
      if (!Number.isFinite(floorId)) return;
      flashFloorByFloorId(floorId);
      draw2DSlice(floorId);
    });
  });
}

function resize() {
  const w = stage.clientWidth;
  const h = stage.clientHeight;
  camera.aspect = w / h;
  camera.updateProjectionMatrix();
  renderer.setSize(w, h);
}

function init3D() {
  renderer.setPixelRatio(window.devicePixelRatio || 1);
  stage.appendChild(renderer.domElement);
  resize();
  window.addEventListener("resize", resize);

  const grid = new THREE.GridHelper(80, 20, 0x2f3f55, 0x253347);
  scene.add(grid);
  const light = new THREE.DirectionalLight(0xffffff, 1.1);
  light.position.set(20, 30, 20);
  scene.add(light);
  scene.add(new THREE.AmbientLight(0x93c5fd, 0.45));

  animate();
}

function animate() {
  requestAnimationFrame(animate);
  renderer.render(scene, camera);
}

function clearFloors() {
  for (const m of floorMeshes) scene.remove(m);
  floorMeshes.length = 0;
}

function buildFloors() {
  clearFloors();
  /* 仅遍历真实楼层；勿用 Math.max(1, length)，否则空列表时 floorData[0] 为 undefined 会报错 */
  const list = (Array.isArray(floorData) ? floorData : []).filter(
    (f) => f && (f.floorId != null || f.FloorId != null)
  );
  // 接口默认按 floor_id 倒序返回；3D 叠层需保持“底部为 1 层”，因此按楼层号升序建模
  list.sort((a, b) => {
    const fa = Number(a.floorId ?? a.FloorId);
    const fb = Number(b.floorId ?? b.FloorId);
    const na = Number.isFinite(fa) ? fa : Number.MAX_SAFE_INTEGER;
    const nb = Number.isFinite(fb) ? fb : Number.MAX_SAFE_INTEGER;
    return na - nb;
  });
  for (let i = 0; i < list.length; i++) {
    const floor = list[i];
    const floorId = floor.floorId ?? floor.FloorId;
    const nodeId = floor.nodeId ?? floor.NodeId;
    const geo = new THREE.BoxGeometry(14, 1.8, 14);
    const mat = new THREE.MeshPhongMaterial({
      color: 0x2b3e57,
      emissive: 0x000000,
      transparent: true,
      opacity: 0.95
    });
    const mesh = new THREE.Mesh(geo, mat);
    mesh.position.y = i * 2.3 + 1;
    mesh.userData = { floorId, nodeId, idx: i };
    scene.add(mesh);
    floorMeshes.push(mesh);
  }
  applySelectedFloorHighlight();
  focusCameraToFloorStack();
}

function draw2DSlice(floorId) {
  currentFloorId = floorId;
  applySelectedFloorHighlight();
  sctx.clearRect(0, 0, slice2d.width, slice2d.height);
  sctx.fillStyle = "#1a2435";
  sctx.fillRect(0, 0, slice2d.width, slice2d.height);
  sctx.strokeStyle = "#2563eb";
  sctx.strokeRect(18, 18, slice2d.width - 36, slice2d.height - 36);
  sctx.fillStyle = "#dbeafe";
  sctx.font = "16px sans-serif";
  sctx.fillText(`第${floorId}层 2D 切片`, 24, 42);

  const cams = cameraData.filter((c) => Number(c.floorId) === Number(floorId));
  for (const c of cams) {
    const x = Number(c.posX);
    const y = Number(c.posY);
    sctx.fillStyle = "#ff5a5f";
    sctx.beginPath();
    sctx.arc(x, y, 6, 0, Math.PI * 2);
    sctx.fill();
    sctx.fillStyle = "#ffffff";
    sctx.font = "12px sans-serif";
    sctx.fillText(`C${c.cameraId}`, x + 8, y - 8);
  }
  setResult({ mode: "2d", floorId, cameraCount: cams.length });
  // 统一由切片入口刷新楼层标签选中态，保证 3D 点击与标签点击一致
  renderFloorChips();
  setMetrics();
}

function reset3DView() {
  currentFloorId = null;
  applySelectedFloorHighlight();
  focusCameraToFloorStack();
  setResult("已返回3D总览");
  renderFloorChips();
  setMetrics();
}

function bindPicking() {
  renderer.domElement.addEventListener("click", (e) => {
    const rect = renderer.domElement.getBoundingClientRect();
    mouse.x = ((e.clientX - rect.left) / rect.width) * 2 - 1;
    mouse.y = -((e.clientY - rect.top) / rect.height) * 2 + 1;
    raycaster.setFromCamera(mouse, camera);
    const hits = raycaster.intersectObjects(floorMeshes, false);
    if (hits.length === 0) return;
    const floorId = hits[0].object.userData.floorId;
    flashFloorByFloorId(floorId);
    draw2DSlice(floorId);
  });
}

function flashFloorByFloorId(floorId) {
  const mesh = floorMeshes.find((m) => Number(m.userData.floorId) === Number(floorId));
  if (!mesh) return;
  const mat = mesh.material;
  const stateKey = "__flashState";
  const prev = mesh.userData[stateKey];
  if (prev && Array.isArray(prev.timers)) {
    prev.timers.forEach((id) => clearTimeout(id));
  }

  const flashOn = FLOOR_FLASH_EMISSIVE;
  const flashOff = FLOOR_DEFAULT_EMISSIVE;
  const cycles = 5;
  const stepMs = 220;
  const timers = [];
  let count = 0;

  const tick = () => {
    const isOn = count % 2 === 0;
    mat.emissive.setHex(isOn ? flashOn : flashOff);
    count += 1;
    if (count < cycles * 2) {
      timers.push(setTimeout(tick, stepMs));
      return;
    }
    applySelectedFloorHighlight();
  };

  mesh.userData[stateKey] = { timers };
  tick();
}

function flashFloorByCameraId(cameraId) {
  const cam = cameraData.find((c) => Number(c.cameraId) === Number(cameraId));
  if (!cam) return;
  flashFloorByFloorId(cam.floorId);
}

async function loadBaseData() {
  const [fRes, cRes] = await Promise.all([
    fetch(`${apiBase}/api/floor/list`, { credentials: "include" }),
    fetch(`${apiBase}/api/camera/list`, { credentials: "include" })
  ]);
  const fData = await fRes.json();
  const cData = await cRes.json();
  const rawFloors = fData.data;
  const rawCams = cData.data;
  floorData = Array.isArray(rawFloors) ? rawFloors : [];
  cameraData = Array.isArray(rawCams) ? rawCams : [];
  buildFloors();
  renderFloorChips();
  setMetrics();
  setResult({
    floorCount: floorData.length,
    cameraCount: cameraData.length,
    hint: floorData.length === 0 ? "暂无楼层数据，请先在「楼层图纸」上传并创建楼层" : undefined
  });
}

async function loadHistoryEventFeed() {
  try {
    const [captureRes, alertRes, trackRes] = await Promise.all([
      fetch(`${apiBase}/api/capture/list?limit=24`, { credentials: "include" }),
      fetch(`${apiBase}/api/alert/list?limit=24`, { credentials: "include" }),
      fetch(`${apiBase}/api/track/history/list?limit=24`, { credentials: "include" })
    ]);
    const captureData = await captureRes.json();
    const alertData = await alertRes.json();
    const trackData = await trackRes.json();
    const captureItems = Array.isArray(captureData?.data)
      ? captureData.data.map((x) => ({
          eventType: "capture",
          title: "抓拍事件",
          detail: `设备${x.deviceId ?? "-"} 通道${x.channelNo ?? "-"} 捕获一条记录`,
          eventTime: x.captureTime
        }))
      : [];
    const alertItems = Array.isArray(alertData?.data)
      ? alertData.data.map((x) => ({
          eventType: "alert",
          title: x.alertType || "告警事件",
          detail: x.detail || "无详情",
          eventTime: x.createdAt
        }))
      : [];
    const trackItems = Array.isArray(trackData?.data)
      ? trackData.data.map((x) => ({
          eventType: "track",
          title: "轨迹事件",
          detail: `VID=${x.vid ?? "-"} 摄像头=${x.cameraId ?? "-"} 防区=${x.roiId ?? "-"}`,
          cameraId: x.cameraId ? Number(x.cameraId) : null,
          eventTime: x.eventTime
        }))
      : [];
    const merged = [...captureItems, ...alertItems, ...trackItems]
      .map((x) => normalizeEventItem(x))
      .sort((a, b) => b.eventTime - a.eventTime)
      .slice(0, EVENT_FEED_LIMIT);
    eventFeedItems.splice(0, eventFeedItems.length, ...merged);
    renderEventFeed();
    setMetrics();
  } catch (error) {
    setResult(`历史事件加载失败：${error.message}`);
  }
}

async function initSignalR() {
  if (!window.signalR) {
    setResult("SignalR脚本未加载");
    return;
  }
  const connection = new window.signalR.HubConnectionBuilder()
    .withUrl(`${apiBase}/hubs/events`, {
      // 纯 Cookie 会话下由浏览器自动携带 HttpOnly Cookie；accessTokenFactory 保持兼容占位
      accessTokenFactory: () => ""
    })
    .withAutomaticReconnect()
    .build();

  connection.on("alert.created", (d) => {
    if (currentFloorId) {
      flashFloorByFloorId(currentFloorId);
    } else if (floorMeshes.length > 0) {
      flashFloorByFloorId(floorMeshes[0].userData.floorId);
    }
    pushEventFeed({
      eventType: "alert",
      title: d?.alertType || "告警事件",
      detail: d?.detail || "无详情",
      eventTime: d?.at || new Date().toISOString()
    });
    setResult({ event: "alert.created", data: d });
    setMetrics();
  });
  connection.on("track.event", (d) => {
    if (d && d.cameraId) flashFloorByCameraId(d.cameraId);
    pushEventFeed({
      eventType: "track",
      title: "轨迹事件",
      detail: `VID=${d?.vid || "-"} 摄像头=${d?.cameraId || "-"}`,
      cameraId: d?.cameraId,
      eventTime: d?.eventTime || new Date().toISOString()
    });
    setMetrics();
  });
  connection.on("capture.received", (d) => {
    if (d && d.deviceId) {
      pushEventFeed({
        eventType: "capture",
        title: "抓拍事件",
        detail: `设备${d.deviceId} 通道${d.channelNo ?? "-"}`,
        eventTime: d?.captureTime || new Date().toISOString()
      });
      setResult({ event: "capture.received", data: d });
      setMetrics();
    }
  });
  try {
    await connection.start();
  } catch (error) {
    setResult(`SignalR连接失败：${error.message}`);
  }
}

document.getElementById("resetBtn").addEventListener("click", reset3DView);
document.getElementById("back3dBtn").addEventListener("click", reset3DView);

init3D();
bindPicking();
bindEventFilter();
loadBaseData();
loadHistoryEventFeed();
initSignalR();
