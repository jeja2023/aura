/* 文件：抓拍页脚本（capture.js） | File: Capture Script */
const apiBase = "";

const resultEl = document.getElementById("result");
const createCaptureResultEl = document.getElementById("createCaptureResult");
const captureCreateModalEl = document.getElementById("captureCreateModal");
const captureOnboardModalEl = document.getElementById("captureOnboardModal");
const openCreateCaptureModalBtn = document.getElementById("openCreateCaptureModal");
const openCaptureOnboardModalBtn = document.getElementById("openCaptureOnboardModal");
const captureTableWrapEl = document.getElementById("captureTableWrap");
const capturePagerEl = document.getElementById("capturePager");
const captureTableHeadEl = document.getElementById("captureTableHead");
const captureTableBodyEl = document.getElementById("captureTableBody");
const exportCaptureBtn = document.getElementById("exportCapture");
const captureDeviceIdFilterEl = document.getElementById("captureDeviceIdFilter");
const captureChannelNoFilterEl = document.getElementById("captureChannelNoFilter");
const captureStartTimeFilterEl = document.getElementById("captureStartTimeFilter");
const captureEndTimeFilterEl = document.getElementById("captureEndTimeFilter");
const clearCaptureFilterBtn = document.getElementById("clearCaptureFilter");
const requestJson = window.aura?.requestJson || fallbackRequestJson;
const pageStatus = window.aura?.createStatusController?.(resultEl) || null;
const createStatus = window.aura?.createStatusController?.(createCaptureResultEl, { successMs: 0 }) || null;

let latestCaptureRows = [];
let latestCapturePager = null;
let capturePage = 1;
let capturePageSize = 15;

function fillCapturePushUrlDoc() {
  const el = document.getElementById("capturePushUrlDoc");
  if (!el) return;
  const origin = (window.location.origin || "").replace(/\/$/, "");
  el.textContent = `${origin}/api/capture/push`;
}

async function fallbackRequestJson(url, options = {}) {
  const headers = new Headers(options.headers || {});
  const init = {
    ...options,
    credentials: options.credentials || "include",
    headers
  };
  const body = options.body;
  const isJsonBody = body && typeof body === "object" && !(body instanceof FormData) && !(body instanceof Blob) && !(body instanceof URLSearchParams);
  if (isJsonBody) {
    if (!headers.has("Content-Type")) headers.set("Content-Type", "application/json");
    init.body = JSON.stringify(body);
  }

  const response = await fetch(url, init);
  const text = await response.text();
  let data = null;
  if (text) {
    try {
      data = JSON.parse(text);
    } catch {
      data = { code: response.ok ? 0 : -1, msg: text };
    }
  }
  return { ok: response.ok, status: response.status, data, response };
}

function escapeHtml(value) {
  if (window.aura && typeof window.aura.escapeHtml === "function") {
    return window.aura.escapeHtml(value);
  }
  return String(value ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#39;");
}

function deriveMessage(data, fallback = "操作完成") {
  if (window.aura && typeof window.aura.deriveMessage === "function") {
    return window.aura.deriveMessage(data, fallback);
  }
  if (typeof data === "string") return data;
  if (data && typeof data === "object") {
    if (typeof data.msg === "string") return data.msg;
    if (Array.isArray(data.data)) return `共 ${data.data.length} 条结果`;
    return fallback;
  }
  return String(data ?? "");
}

function isErrorPayload(data, message = "") {
  if (window.aura && typeof window.aura.isErrorPayload === "function") {
    return window.aura.isErrorPayload(data, message);
  }
  if (data && typeof data === "object" && typeof data.code === "number") {
    return data.code !== 0;
  }
  return /失败|错误|异常|超时|拒绝|未授权|无权|禁止|非法|无效|无法|不能|不存在|已过期|已失效/.test(String(message || ""));
}

function setStatus(controller, element, data, options = {}) {
  if (controller && typeof controller.set === "function") {
    controller.set(data, options);
    return;
  }
  if (window.aura && typeof window.aura.setStatus === "function") {
    window.aura.setStatus(element, data, options);
    return;
  }
  if (!element) return;
  const message = deriveMessage(data, options.fallback || "操作完成").trim();
  if (!message) {
    element.textContent = "";
    element.hidden = true;
    element.classList.remove("is-error");
    return;
  }
  const isError = options.isError ?? isErrorPayload(data, message);
  element.textContent = message;
  element.hidden = false;
  element.classList.toggle("is-error", Boolean(isError));
}

function clearStatus(controller, element) {
  if (controller && typeof controller.clear === "function") {
    controller.clear();
    return;
  }
  setStatus(null, element, "");
}

function setResult(data, options = {}) {
  setStatus(pageStatus, resultEl, data, options);
}

function setCreateCaptureResult(data, options = {}) {
  setStatus(createStatus, createCaptureResultEl, data, options);
}

function setExportVisible(visible) {
  if (!exportCaptureBtn) return;
  if (window.aura && typeof window.aura.setElementVisible === "function") {
    window.aura.setElementVisible(exportCaptureBtn, visible);
    return;
  }
  exportCaptureBtn.hidden = !visible;
  exportCaptureBtn.disabled = !visible;
}

function openModal(root, options = {}) {
  if (!root) return;
  if (window.aura && typeof window.aura.openModal === "function") {
    window.aura.openModal(root, options);
    return;
  }
  root.hidden = false;
  document.body.style.overflow = "hidden";
}

function closeModal(root) {
  if (!root) return;
  if (window.aura && typeof window.aura.closeModal === "function") {
    window.aura.closeModal(root);
    return;
  }
  root.hidden = true;
  document.body.style.overflow = "";
}

function openCaptureCreateModal() {
  clearStatus(createStatus, createCaptureResultEl);
  openModal(captureCreateModalEl, { focus: "#deviceId" });
}

function openCaptureOnboardModal() {
  fillCapturePushUrlDoc();
  openModal(captureOnboardModalEl);
}

function hideTable() {
  if (capturePagerEl) {
    capturePagerEl.hidden = true;
    capturePagerEl.innerHTML = "";
  }
  if (captureTableHeadEl) captureTableHeadEl.innerHTML = "";
  if (captureTableBodyEl) captureTableBodyEl.innerHTML = "";
  if (captureTableWrapEl) captureTableWrapEl.hidden = true;
}

function formatTime(value) {
  const formatter = window.aura?.formatDateTime || window.formatDateTimeDisplay;
  if (typeof formatter === "function") return formatter(value, "-");
  return String(value ?? "-");
}

function parseMetadataJson(raw) {
  const text = String(raw ?? "").trim();
  if (!text) return null;
  try {
    const parsed = JSON.parse(text);
    return parsed && typeof parsed === "object" ? parsed : null;
  } catch {
    return null;
  }
}

function hasOwn(obj, key) {
  return Object.prototype.hasOwnProperty.call(obj, key);
}

function buildCaptureBadge(label, tone = "neutral") {
  return `<span class="capture-badge is-${escapeHtml(tone)}">${escapeHtml(label)}</span>`;
}

function formatKeyValueList(map) {
  const entries = Object.entries(map || {}).filter(([key, value]) => {
    if (!key || key.startsWith("ai_")) return false;
    return value !== null && value !== undefined && String(value).trim() !== "";
  });
  if (!entries.length) return "";
  return entries
    .slice(0, 4)
    .map(([key, value]) => `<span>${escapeHtml(key)}=${escapeHtml(value)}</span>`)
    .join("");
}

function formatImageCell(pathValue) {
  const raw = String(pathValue ?? "").trim();
  if (!raw) return '<span class="capture-image-empty">未归档</span>';
  const href = raw.startsWith("/") ? raw : `/${raw.replace(/^\.?\//, "")}`;
  return `<a class="capture-image-link" href="${escapeHtml(href)}" target="_blank" rel="noopener noreferrer">${escapeHtml(raw)}</a>`;
}

function formatMetadataCell(row) {
  const raw = row?.metadataJson ?? row?.MetadataJson ?? "";
  const meta = parseMetadataJson(raw);
  if (!meta) {
    return `<div class="capture-meta"><div class="capture-meta-summary">原始元数据</div><div class="capture-meta-raw">${escapeHtml(raw || "-")}</div></div>`;
  }

  const aiStatus = String(meta.ai_status || "").trim();
  const badges = [];
  if (aiStatus === "ready") {
    badges.push(buildCaptureBadge("AI+向量就绪", "ok"));
  } else if (aiStatus === "vector_retry_pending" || aiStatus === "extract_retry_pending") {
    badges.push(buildCaptureBadge("补偿排队中", "warn"));
  } else if (aiStatus === "vector_failed" || aiStatus === "extract_failed") {
    badges.push(buildCaptureBadge("链路失败", "error"));
  } else if (aiStatus === "extract_only") {
    badges.push(buildCaptureBadge("仅提特征", "neutral"));
  }

  if (hasOwn(meta, "ai_success")) {
    badges.push(buildCaptureBadge(meta.ai_success === true ? "提特征成功" : "提特征失败", meta.ai_success === true ? "ok" : "error"));
  }
  if (hasOwn(meta, "ai_vector_success") || meta.ai_vector_id) {
    const vectorOk = meta.ai_vector_success === true;
    const retryQueued = meta.ai_retry_queued === true;
    badges.push(buildCaptureBadge(vectorOk ? "向量已写入" : "向量待确认", vectorOk ? "ok" : retryQueued ? "warn" : "neutral"));
  }
  if (meta.ai_retry_queued === true) {
    badges.push(buildCaptureBadge("已入重试队列", "warn"));
  }

  const summaryParts = [];
  if (meta.ai_msg) summaryParts.push(`AI：${meta.ai_msg}`);
  if (meta.ai_vector_msg) summaryParts.push(`向量：${meta.ai_vector_msg}`);
  const extraFields = [];
  if (meta.ai_vector_id) extraFields.push(`<span>向量ID：${escapeHtml(meta.ai_vector_id)}</span>`);
  if (meta.ai_vector_engine) extraFields.push(`<span>引擎：${escapeHtml(meta.ai_vector_engine)}</span>`);
  if (Number.isFinite(Number(meta.ai_dim)) && Number(meta.ai_dim) > 0) {
    extraFields.push(`<span>维度：${escapeHtml(meta.ai_dim)}</span>`);
  }
  if (meta.ai_retry_reason) extraFields.push(`<span>补偿说明：${escapeHtml(meta.ai_retry_reason)}</span>`);
  const customFields = formatKeyValueList(meta);
  const rawJson = escapeHtml(JSON.stringify(meta, null, 2));

  return `
    <div class="capture-meta">
      ${badges.length ? `<div class="capture-meta-badges">${badges.join("")}</div>` : ""}
      <div class="capture-meta-summary">${escapeHtml(summaryParts.join("；") || "未写入 AI 链路摘要")}</div>
      ${extraFields.length ? `<div class="capture-meta-fields">${extraFields.join("")}</div>` : ""}
      ${customFields ? `<div class="capture-meta-extra">${customFields}</div>` : ""}
      <details class="capture-meta-details">
        <summary>查看原始元数据</summary>
        <pre>${rawJson}</pre>
      </details>
    </div>
  `;
}

function getPositiveIntegerInput(element) {
  const text = String(element?.value ?? "").trim();
  if (!text) return null;
  const value = Number(text);
  return Number.isInteger(value) && value > 0 ? value : null;
}

function getDateTimeInput(element) {
  const text = String(element?.value ?? "").trim();
  return text || null;
}

function getCaptureFilters() {
  return {
    deviceId: getPositiveIntegerInput(captureDeviceIdFilterEl),
    channelNo: getPositiveIntegerInput(captureChannelNoFilterEl),
    from: getDateTimeInput(captureStartTimeFilterEl),
    to: getDateTimeInput(captureEndTimeFilterEl)
  };
}

function appendCaptureFilters(query) {
  const filters = getCaptureFilters();
  if (filters.deviceId) query.set("deviceId", String(filters.deviceId));
  if (filters.channelNo) query.set("channelNo", String(filters.channelNo));
  if (filters.from) query.set("from", filters.from);
  if (filters.to) query.set("to", filters.to);
}

function clearCaptureFilters() {
  if (captureDeviceIdFilterEl) captureDeviceIdFilterEl.value = "";
  if (captureChannelNoFilterEl) captureChannelNoFilterEl.value = "";
  if (captureStartTimeFilterEl) captureStartTimeFilterEl.value = "";
  if (captureEndTimeFilterEl) captureEndTimeFilterEl.value = "";
  reloadFirstPage();
}

function normalizeServerPager(pager, rows) {
  if (!pager || typeof pager !== "object") return null;
  const total = Math.max(0, Number(pager.total ?? rows.length) || 0);
  const pageSize = Math.max(1, Number(pager.pageSize ?? capturePageSize) || capturePageSize);
  const totalPages = Math.max(1, Math.ceil(total / pageSize));
  const page = Math.min(totalPages, Math.max(1, Number(pager.page ?? capturePage) || capturePage));
  return { page, pageSize, total, totalPages };
}

function renderCaptureTable(rows, serverPager = latestCapturePager) {
  const list = Array.isArray(rows) ? rows : [];
  const pagerApi = window.aura && typeof window.aura.paginateArray === "function" ? window.aura : null;
  const normalizedPager = normalizeServerPager(serverPager, list);
  const pageData = normalizedPager
    ? { rows: list, ...normalizedPager }
    : pagerApi
      ? pagerApi.paginateArray(list, capturePage, capturePageSize)
      : { rows: list, page: 1, pageSize: list.length || 20, total: list.length, totalPages: 1 };
  capturePage = pageData.page;
  capturePageSize = pageData.pageSize;
  if (!captureTableHeadEl || !captureTableBodyEl) return;
  captureTableHeadEl.innerHTML = `<tr>
    <th class="aura-col-no">序号</th>
    <th class="aura-col-id">抓拍ID</th>
    <th class="aura-col-id">设备ID</th>
    <th>通道号</th>
    <th class="aura-col-time">抓拍时间</th>
    <th class="capture-col-meta">AI / 元数据</th>
    <th>图片路径</th>
  </tr>`;
  if (!pageData.rows.length) {
    captureTableBodyEl.innerHTML = '<tr><td colspan="7">暂无抓拍数据。</td></tr>';
  } else {
    const start = (pageData.page - 1) * pageData.pageSize;
    captureTableBodyEl.innerHTML = pageData.rows
      .map((row, index) => `<tr>
        <td class="aura-col-no">${start + index + 1}</td>
        <td class="aura-col-id">${escapeHtml(row.captureId ?? row.CaptureId ?? "-")}</td>
        <td class="aura-col-id">${escapeHtml(row.deviceId ?? row.DeviceId ?? "-")}</td>
        <td>${escapeHtml(row.channelNo ?? row.ChannelNo ?? "-")}</td>
        <td class="aura-col-time">${escapeHtml(formatTime(row.captureTime ?? row.CaptureTime))}</td>
        <td class="capture-col-meta">${formatMetadataCell(row)}</td>
        <td>${formatImageCell(row.imagePath ?? row.ImagePath ?? "")}</td>
      </tr>`)
      .join("");
  }
  if (captureTableWrapEl) captureTableWrapEl.hidden = false;
  if (capturePagerEl && window.aura && typeof window.aura.renderPager === "function") {
    window.aura.renderPager(capturePagerEl, {
      page: pageData.page,
      pageSize: pageData.pageSize,
      total: pageData.total,
      pageSizeOptions: [15, 30, 45, 60],
      onChange: (nextPage, nextPageSize) => {
        capturePage = nextPage;
        capturePageSize = nextPageSize;
        if (latestCapturePager) {
          void load();
        } else {
          renderCaptureTable(latestCaptureRows);
        }
      }
    });
  }
}

async function createMock() {
  const deviceId = Number(document.getElementById("deviceId")?.value || 1);
  const channelNo = Number(document.getElementById("channelNo")?.value || 1);
  const metadataJson = document.getElementById("meta")?.value || "";
  clearStatus(createStatus, createCaptureResultEl);

  try {
    const result = await requestJson(`${apiBase}/api/capture/mock`, {
      method: "POST",
      body: { deviceId, channelNo, metadataJson }
    });
    const data = result.data || {};
    setCreateCaptureResult(data);
    if (result.ok && data?.code === 0) {
      const deviceIdEl = document.getElementById("deviceId");
      const channelNoEl = document.getElementById("channelNo");
      const metaEl = document.getElementById("meta");
      if (deviceIdEl instanceof HTMLInputElement) deviceIdEl.value = "";
      if (channelNoEl instanceof HTMLInputElement) channelNoEl.value = "";
      if (metaEl instanceof HTMLInputElement) metaEl.value = "";
      closeModal(captureCreateModalEl);
      capturePage = 1;
      void load();
    }
  } catch (error) {
    setCreateCaptureResult({ code: 40000, msg: `新增失败：${normalizeErrorMessage(error)}` }, { isError: true });
  }
}

function normalizeErrorMessage(error) {
  return error instanceof Error ? error.message : String(error ?? "未知错误");
}

async function load() {
  clearStatus(pageStatus, resultEl);
  hideTable();
  setExportVisible(false);

  try {
    const query = new URLSearchParams({
      page: String(capturePage),
      pageSize: String(capturePageSize)
    });
    appendCaptureFilters(query);
    const result = await requestJson(`${apiBase}/api/capture/list?${query.toString()}`);
    const data = result.data || {};
    if (!result.ok || data?.code !== 0) {
      setResult(data?.msg ? data : { code: 40000, msg: `查询失败：HTTP ${result.status}` }, { isError: true });
      latestCaptureRows = [];
      latestCapturePager = null;
      setExportVisible(false);
      return;
    }
    latestCaptureRows = Array.isArray(data.data) ? data.data : [];
    latestCapturePager = normalizeServerPager(data.pagination, latestCaptureRows);
    renderCaptureTable(latestCaptureRows, latestCapturePager);
    setExportVisible((latestCapturePager?.total ?? latestCaptureRows.length) > 0);
  } catch (error) {
    setResult({ code: 40000, msg: `查询失败：${normalizeErrorMessage(error)}` }, { isError: true });
    latestCaptureRows = [];
    latestCapturePager = null;
    setExportVisible(false);
  }
}

function reloadFirstPage() {
  capturePage = 1;
  void load();
}

openCreateCaptureModalBtn?.addEventListener("click", openCaptureCreateModal);
openCaptureOnboardModalBtn?.addEventListener("click", openCaptureOnboardModal);

if (window.aura && typeof window.aura.bindModalDismiss === "function") {
  window.aura.bindModalDismiss(captureCreateModalEl, { onClose: () => closeModal(captureCreateModalEl) });
  window.aura.bindModalDismiss(captureOnboardModalEl, {
    dismissSelector: "[data-capture-onboard-dismiss]",
    onClose: () => closeModal(captureOnboardModalEl)
  });
} else {
  captureCreateModalEl?.querySelectorAll("[data-aura-modal-dismiss], .aura-modal-backdrop").forEach((el) => {
    el.addEventListener("click", () => closeModal(captureCreateModalEl));
  });
  captureOnboardModalEl?.querySelectorAll("[data-capture-onboard-dismiss], .aura-modal-backdrop").forEach((el) => {
    el.addEventListener("click", () => closeModal(captureOnboardModalEl));
  });
}

document.getElementById("load")?.addEventListener("click", reloadFirstPage);
clearCaptureFilterBtn?.addEventListener("click", clearCaptureFilters);
[
  captureDeviceIdFilterEl,
  captureChannelNoFilterEl,
  captureStartTimeFilterEl,
  captureEndTimeFilterEl
].forEach((element) => {
  element?.addEventListener("keydown", (event) => {
    if (event.key === "Enter") {
      event.preventDefault();
      reloadFirstPage();
    }
  });
});
document.getElementById("create")?.addEventListener("click", createMock);
exportCaptureBtn?.addEventListener("click", async (event) => {
  event.preventDefault();
  event.stopPropagation();
  if (window.aura && typeof window.aura.exportDataset === "function") {
    await window.aura.exportDataset({
      apiBase,
      dataset: "capture",
      params: {
        ...getCaptureFilters(),
        maxRows: 20000
      },
      onError: (message) => setResult(message)
    });
    return;
  }
  setResult({ code: 40000, msg: "导出失败：缺少全局导出能力" }, { isError: true });
});

fillCapturePushUrlDoc();
void load();
