/* 文件：三维态势页脚本（scene.js） | File: Scene Script */
const apiBase = "https://localhost:5001";
const resultEl = document.getElementById("result");
const stage = document.getElementById("stage3d");
const slice2d = document.getElementById("slice2d");
const sctx = slice2d.getContext("2d");
const floorMeshes = [];
let floorData = [];
let cameraData = [];
let currentFloorId = null;

const scene = new THREE.Scene();
scene.background = new THREE.Color(0x101826);
const camera = new THREE.PerspectiveCamera(60, 1, 0.1, 1000);
camera.position.set(28, 24, 28);
camera.lookAt(0, 6, 0);
const renderer = new THREE.WebGLRenderer({ antialias: true });
const raycaster = new THREE.Raycaster();
const mouse = new THREE.Vector2();

function getToken() {
  return localStorage.getItem("token") ?? "";
}

function setResult(data) {
  resultEl.textContent = typeof data === "string" ? data : JSON.stringify(data, null, 2);
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
  scene.add(new THREE.AmbientLight(0xaaccff, 0.5));

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
  const count = Math.max(1, floorData.length);
  for (let i = 0; i < count; i++) {
    const floor = floorData[i];
    const geo = new THREE.BoxGeometry(14, 1.8, 14);
    const mat = new THREE.MeshPhongMaterial({
      color: 0x2b3e57,
      emissive: 0x000000,
      transparent: true,
      opacity: 0.95
    });
    const mesh = new THREE.Mesh(geo, mat);
    mesh.position.y = i * 2.3 + 1;
    mesh.userData = { floorId: floor.floorId, nodeId: floor.nodeId, idx: i };
    scene.add(mesh);
    floorMeshes.push(mesh);
  }
}

function draw2DSlice(floorId) {
  currentFloorId = floorId;
  sctx.clearRect(0, 0, slice2d.width, slice2d.height);
  sctx.fillStyle = "#1a2435";
  sctx.fillRect(0, 0, slice2d.width, slice2d.height);
  sctx.strokeStyle = "#00d2ff";
  sctx.strokeRect(18, 18, slice2d.width - 36, slice2d.height - 36);
  sctx.fillStyle = "#d9e6ff";
  sctx.font = "16px sans-serif";
  sctx.fillText(`Floor #${floorId} 2D Slice`, 24, 42);

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
}

function reset3DView() {
  currentFloorId = null;
  camera.position.set(28, 24, 28);
  camera.lookAt(0, 6, 0);
  setResult("已返回3D总览");
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
    draw2DSlice(floorId);
  });
}

function flashFloorByFloorId(floorId) {
  const mesh = floorMeshes.find((m) => Number(m.userData.floorId) === Number(floorId));
  if (!mesh) return;
  mesh.material.emissive.setHex(0xff2222);
  setTimeout(() => mesh.material.emissive.setHex(0x000000), 1000);
}

function flashFloorByCameraId(cameraId) {
  const cam = cameraData.find((c) => Number(c.cameraId) === Number(cameraId));
  if (!cam) return;
  flashFloorByFloorId(cam.floorId);
}

async function loadBaseData() {
  const headers = { Authorization: `Bearer ${getToken()}` };
  const [fRes, cRes] = await Promise.all([
    fetch(`${apiBase}/api/floor/list`, { headers }),
    fetch(`${apiBase}/api/camera/list`, { headers })
  ]);
  const fData = await fRes.json();
  const cData = await cRes.json();
  floorData = fData.data || [];
  cameraData = cData.data || [];
  buildFloors();
  setResult({ floorCount: floorData.length, cameraCount: cameraData.length });
}

async function initSignalR() {
  if (!window.signalR) {
    setResult("SignalR脚本未加载");
    return;
  }
  const connection = new window.signalR.HubConnectionBuilder()
    .withUrl(`${apiBase}/hubs/events`)
    .withAutomaticReconnect()
    .build();

  connection.on("alert.created", (d) => {
    if (currentFloorId) {
      flashFloorByFloorId(currentFloorId);
    } else if (floorMeshes.length > 0) {
      flashFloorByFloorId(floorMeshes[0].userData.floorId);
    }
    setResult({ event: "alert.created", data: d });
  });
  connection.on("track.event", (d) => {
    if (d && d.cameraId) flashFloorByCameraId(d.cameraId);
  });
  connection.on("capture.received", (d) => {
    if (d && d.deviceId) setResult({ event: "capture.received", data: d });
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
loadBaseData();
initSignalR();
