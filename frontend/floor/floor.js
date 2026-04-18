/* 文件：楼层页脚本（floor.js） | File: Floor Script */
const apiBase = "";
const resultEl = document.getElementById("result");
const previewEl = document.getElementById("preview");
const floorListEl = document.getElementById("floorList");
const floorKeywordEl = document.getElementById("floorKeyword");
const floorCountEl = document.getElementById("floorCount");
const openPreviewBtn = document.getElementById("openPreviewBtn");
const openUploadModalBtn = document.getElementById("openUploadModalBtn");
const refreshBtnTop = document.getElementById("refreshBtnTop");
const preferToast = Boolean(window.aura && typeof window.aura.toast === "function");
let localPreviewUrl = "";
let latestRows = [];
let selectedFloorId = null;
const staticPreviewPlaceholderCandidates = [];
const builtInPreviewPlaceholderUrl = `data:image/svg+xml;charset=UTF-8,${encodeURIComponent(
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
      <text x="640" y="340" fill="#e2e8f0" font-size="40">楼层图加载失败</text>
      <text x="640" y="390" fill="#94a3b8" font-size="24">已自动回退占位图</text>
    </g>
  </svg>`
)}`;
let resolvedPreviewPlaceholderUrl = "";
let previewFallbacking = false;

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

function setResult(data) {
  const isEmpty = !data || (typeof data === "string" && data.trim() === "");
  if (isEmpty) {
    clearSuccessStatusTimer();
    if (!preferToast) hideResult();
    return;
  }

  const message = deriveMessage(data);
  const isError = isErrorPayload(data, message);

  clearSuccessStatusTimer();
  if (preferToast) {
    window.aura.toast(message, isError);
  } else if (resultEl) {
    resultEl.textContent = message;
    resultEl.hidden = false;
    resultEl.classList.toggle("is-error", isError);
  }

  if (!isError) {
    successStatusTimer = window.setTimeout(() => {
      successStatusTimer = null;
      if (!preferToast) hideResult();
    }, SUCCESS_STATUS_MS);
  }
}

function clearLocalPreviewUrl() {
  if (!localPreviewUrl) return;
  URL.revokeObjectURL(localPreviewUrl);
  localPreviewUrl = "";
}

function setPreviewSource(src, altText = "楼层图预览") {
  if (!previewEl) return;
  const safeSrc = String(src || "").trim();
  if (!safeSrc) {
    previewEl.removeAttribute("src");
    previewEl.classList.add("is-empty");
    previewEl.alt = altText;
    if (openPreviewBtn) {
      openPreviewBtn.disabled = true;
      openPreviewBtn.setAttribute("aria-disabled", "true");
    }
    return;
  }
  previewEl.alt = altText;
  previewEl.classList.remove("is-empty");
  previewEl.src = safeSrc;
  if (openPreviewBtn) {
    openPreviewBtn.disabled = false;
    openPreviewBtn.removeAttribute("aria-disabled");
  }
}

function openPreviewInNewTab() {
  const src = String(previewEl?.getAttribute("src") || "").trim();
  if (!src) return;
  window.open(src, "_blank", "noopener,noreferrer");
}

function openUploadModal() {
  const root = document.createElement("div");
  root.className = "aura-modal-root";
  root.innerHTML = `<div class="aura-modal-backdrop" tabindex="-1" aria-hidden="true"></div>
<div class="aura-modal-panel" role="dialog" aria-modal="true" aria-labelledby="auraFloorUploadTitle">
  <div class="aura-modal-head">
    <h2 id="auraFloorUploadTitle" class="aura-modal-title">上传与创建楼层图</h2>
    <button type="button" class="btn-secondary aura-modal-close" data-floor-modal-cancel>取消</button>
  </div>
  <p class="floor-modal-lead">选择图纸文件，填写节点与缩放比例后创建楼层图。</p>
  <div class="aura-modal-fields">
    <input id="floorModalFile" type="file" accept=".png,.jpg,.jpeg,.webp,.svg" aria-label="选择楼层图文件" />
    <div class="floor-modal-row">
      <label>资源节点ID <input id="floorModalNodeId" class="aura-input" type="number" value="3" /></label>
      <label>缩放比例（%） <input id="floorModalScalePercent" class="aura-input" type="number" step="0.1" value="100" /></label>
    </div>
  </div>
  <div class="aura-modal-actions">
    <button type="button" class="btn-secondary" data-floor-modal-cancel>取消</button>
    <button type="button" class="btn-primary" id="floorModalUploadBtn">上传并创建</button>
  </div>
</div>`;

  const prevOverflow = document.body.style.overflow;
  document.body.appendChild(root);
  document.body.style.overflow = "hidden";

  const fileInput = root.querySelector("#floorModalFile");
  const nodeIdInput = root.querySelector("#floorModalNodeId");
  const scalePercentInput = root.querySelector("#floorModalScalePercent");
  const uploadBtn = root.querySelector("#floorModalUploadBtn");

  const close = () => {
    if (!root.isConnected) return;
    root.remove();
    document.body.style.overflow = prevOverflow;
    clearLocalPreviewUrl();
  };

  // 需求：弹窗区域外（遮罩）不可关闭；只允许显式按钮关闭
  root.querySelectorAll("[data-floor-modal-cancel]").forEach((el) => {
    el.addEventListener("click", () => close());
  });

  fileInput?.addEventListener("change", () => {
    const file = fileInput.files?.[0];
    clearLocalPreviewUrl();
    if (!file) {
      setPreviewSource("", "楼层图预览");
      return;
    }
    localPreviewUrl = URL.createObjectURL(file);
    setPreviewSource(localPreviewUrl, "楼层图本地预览");
  });

  uploadBtn?.addEventListener("click", async () => {
    const ok = await uploadAndCreateWithInputs({
      fileInput,
      nodeIdInput,
      scalePercentInput
    });
    if (ok) close();
  });
}

function normalizeFilePathToUrl(filePath) {
  return window.normalizeFloorImagePathToUrl(filePath, apiBase);
}

function getDynamicPreviewPlaceholderCandidates() {
  return latestRows
    .map((row) => normalizeFilePathToUrl(row?.filePath ?? row?.FilePath ?? ""))
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

async function resolvePreviewPlaceholderUrl(extraCandidates = []) {
  const candidates = [...extraCandidates, ...getDynamicPreviewPlaceholderCandidates(), ...staticPreviewPlaceholderCandidates];
  for (const candidate of candidates) {
    if (!candidate) continue;
    if (resolvedPreviewPlaceholderUrl && candidate === resolvedPreviewPlaceholderUrl) {
      return resolvedPreviewPlaceholderUrl;
    }
    const ok = await probeImageLoadable(candidate);
    if (ok) {
      resolvedPreviewPlaceholderUrl = candidate;
      return resolvedPreviewPlaceholderUrl;
    }
  }
  resolvedPreviewPlaceholderUrl = builtInPreviewPlaceholderUrl;
  return resolvedPreviewPlaceholderUrl;
}

function loadPreviewFallback(failedSrc = "") {
  const preferred = failedSrc ? [failedSrc] : [];
  void resolvePreviewPlaceholderUrl(preferred).then((fallbackUrl) => {
    previewFallbacking = true;
    setPreviewSource(fallbackUrl, "楼层图占位图");
    setResult("楼层图加载失败，已自动回退占位图");
  });
}

function normalizeSearchText(value) {
  return String(value ?? "")
    .trim()
    .toLowerCase();
}

function getFilteredRows(rows) {
  const list = Array.isArray(rows) ? rows : [];
  const keyword = normalizeSearchText(floorKeywordEl?.value);
  if (!keyword) return list;
  return list.filter((row) => {
    const floorId = row?.floorId ?? row?.FloorId ?? "";
    const nodeId = row?.nodeId ?? row?.NodeId ?? "";
    const hay = normalizeSearchText(`${floorId} ${nodeId}`);
    return hay.includes(keyword);
  });
}

function renderFloorList() {
  if (!floorListEl) return;
  const rows = getFilteredRows(latestRows);
  if (floorCountEl) {
    const total = Array.isArray(latestRows) ? latestRows.length : 0;
    const shown = Array.isArray(rows) ? rows.length : 0;
    floorCountEl.textContent = keywordIsActive()
      ? `显示 ${shown} / 共 ${total}`
      : `共 ${total} 条`;
  }
  if (!latestRows.length) {
    floorListEl.innerHTML = "<div class=\"floor-list-item\"><span>暂无楼层图记录。</span></div>";
    return;
  }
  if (!rows.length) {
    floorListEl.innerHTML = "<div class=\"floor-list-item\"><span>未找到匹配的楼层图。</span></div>";
    return;
  }
  floorListEl.innerHTML = rows
    .map((row) => {
      const floorId = Number(row?.floorId ?? row?.FloorId ?? 0);
      const nodeId = row?.nodeId ?? row?.NodeId ?? "-";
      const activeClass = Number(selectedFloorId) === floorId ? " is-active" : "";
      return `<button type="button" class="floor-list-item${activeClass}" data-floor-id="${floorId}">
        <div class="floor-item-row">
          <span class="floor-item-title">${floorId}层</span>
          <span class="floor-badge">节点 ${escapeHtml(nodeId)}</span>
        </div>
      </button>`;
    })
    .join("");
  floorListEl.querySelectorAll("[data-floor-id]").forEach((btn) => {
    btn.addEventListener("click", () => {
      const floorId = Number(btn.getAttribute("data-floor-id"));
      const row = latestRows.find((x) => Number(x?.floorId ?? x?.FloorId) === floorId);
      if (!row) return;
      selectedFloorId = floorId;
      const filePath = row?.filePath ?? row?.FilePath ?? "";
      const nodeId = row?.nodeId ?? row?.NodeId ?? "-";
      const previewUrl = normalizeFilePathToUrl(filePath);
      setPreviewSource(previewUrl, `楼层图 #${floorId}`);
      setResult(`已切换楼层图：楼层${floorId}，节点${nodeId}`);
      renderFloorList();
    });
  });
}

function keywordIsActive() {
  return Boolean(normalizeSearchText(floorKeywordEl?.value));
}

function escapeHtml(text) {
  return String(text ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll("\"", "&quot;")
    .replaceAll("'", "&#39;");
}

async function uploadAndCreateWithInputs({ fileInput, nodeIdInput, scalePercentInput }) {
  const file = fileInput?.files?.[0];
  const nodeId = Number(nodeIdInput?.value);
  const scalePercent = Number(scalePercentInput?.value);
  const scaleRatio = scalePercent / 100;
  if (!file) {
    setResult("请先选择文件");
    return false;
  }
  if (!Number.isFinite(scalePercent) || scalePercent <= 0) {
    setResult("请填写有效的缩放比例（百分比），例如：100");
    return false;
  }

  setResult("");
  try {
    const form = new FormData();
    form.append("file", file);
    const uploadRes = await fetch(`${apiBase}/api/floor/upload`, {
      method: "POST",
      credentials: "include",
      body: form
    });
    const uploadData = await uploadRes.json();
    if (uploadData.code !== 0) {
      setResult(uploadData);
      return false;
    }

    const filePath = uploadData?.data?.filePath;
    const previewUrl = normalizeFilePathToUrl(filePath);
    setPreviewSource(previewUrl, "楼层图预览");
    const createRes = await fetch(`${apiBase}/api/floor/create`, {
      method: "POST",
      credentials: "include",
      headers: {
        "Content-Type": "application/json"
      },
      body: JSON.stringify({ nodeId, filePath, scaleRatio })
    });
    const createData = await createRes.json();
    setResult({ upload: uploadData, create: createData });
    if (createData?.code === 0) {
      await loadList();
      return true;
    }
    return false;
  } catch (error) {
    setResult(`上传失败：${error.message}`);
    return false;
  }
}

async function loadList() {
  setResult("");
  try {
    const res = await fetch(`${apiBase}/api/floor/list`, {
      credentials: "include"
    });
    const data = await res.json();
    const rows = Array.isArray(data?.data) ? data.data : [];
    if (!res.ok || data?.code !== 0) {
      setResult(data?.msg || "楼层列表加载失败");
      return;
    }
    if (!rows.length) {
      latestRows = [];
      selectedFloorId = null;
      renderFloorList();
      setPreviewSource("", "暂无楼层图");
      setResult("暂无楼层图，请先上传并创建。");
      return;
    }
    latestRows = rows;
    const latest = rows[0];
    const filePath = latest?.filePath ?? latest?.FilePath ?? "";
    const floorId = latest?.floorId ?? latest?.FloorId ?? "-";
    const nodeId = latest?.nodeId ?? latest?.NodeId ?? "-";
    selectedFloorId = Number(floorId) || null;
    const previewUrl = normalizeFilePathToUrl(filePath);
    setPreviewSource(previewUrl, `楼层图 #${floorId}`);
    renderFloorList();
    setResult(`已加载最新楼层图：楼层${floorId}，节点${nodeId}`);
  } catch (error) {
    setResult(`查询失败：${error.message}`);
  }
}

if (previewEl) {
  previewEl.classList.add("is-empty");
  previewEl.addEventListener("error", () => {
    const failedSrc = String(previewEl.getAttribute("src") || "").trim();
    if (previewFallbacking) {
      previewFallbacking = false;
      setPreviewSource("", "楼层图加载失败");
      setResult("楼层图加载失败，且占位图加载失败");
      return;
    }
    loadPreviewFallback(failedSrc);
  });
  previewEl.addEventListener("load", () => {
    previewFallbacking = false;
  });
  previewEl.addEventListener("click", () => openPreviewInNewTab());
}

floorKeywordEl?.addEventListener("input", () => renderFloorList());
openPreviewBtn?.addEventListener("click", () => openPreviewInNewTab());
openUploadModalBtn?.addEventListener("click", () => openUploadModal());
refreshBtnTop?.addEventListener("click", loadList);
void loadList();
if (preferToast && resultEl) {
  resultEl.hidden = true;
}
