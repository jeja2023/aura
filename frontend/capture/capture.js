/* 文件：抓拍页脚本（capture.js） | File: Capture Script */
const apiBase = "";
const resultEl = document.getElementById("result");
const createCaptureResultEl = document.getElementById("createCaptureResult");
const captureCreateModalEl = document.getElementById("captureCreateModal");
const openCreateCaptureModalBtn = document.getElementById("openCreateCaptureModal");
const captureTableWrapEl = document.getElementById("captureTableWrap");
const capturePagerEl = document.getElementById("capturePager");
const captureTableHeadEl = document.getElementById("captureTableHead");
const captureTableBodyEl = document.getElementById("captureTableBody");
const exportCaptureBtn = document.getElementById("exportCapture");
let latestCaptureRows = [];
let capturePage = 1;
let capturePageSize = 15;

function setExportVisible(visible) {
  if (!exportCaptureBtn) return;
  if (window.aura && typeof window.aura.setElementVisible === "function") {
    window.aura.setElementVisible(exportCaptureBtn, visible);
    return;
  }
  exportCaptureBtn.hidden = !visible;
  exportCaptureBtn.disabled = !visible;
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

function hideCreateCaptureResult() {
  if (!createCaptureResultEl) return;
  createCaptureResultEl.textContent = "";
  createCaptureResultEl.hidden = true;
  createCaptureResultEl.classList.remove("is-error");
}

function setCreateCaptureResult(data) {
  if (!createCaptureResultEl) return;
  const isEmpty = !data || (typeof data === "string" && data.trim() === "");
  if (isEmpty) {
    hideCreateCaptureResult();
    return;
  }
  const message = deriveMessage(data);
  const isError = isErrorPayload(data, message);
  createCaptureResultEl.textContent = message;
  createCaptureResultEl.hidden = false;
  createCaptureResultEl.classList.toggle("is-error", isError);
}

function closeCaptureCreateModal() {
  if (!captureCreateModalEl) return;
  captureCreateModalEl.hidden = true;
  document.body.style.overflow = "";
}

function openCaptureCreateModal() {
  if (!captureCreateModalEl) return;
  captureCreateModalEl.hidden = false;
  document.body.style.overflow = "hidden";
  hideCreateCaptureResult();
  const deviceIdEl = document.getElementById("deviceId");
  if (deviceIdEl instanceof HTMLInputElement) deviceIdEl.focus();
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
    return /失败|错误|异常|请/.test(message);
  }
  return false;
}

function escapeHtml(value) {
  return String(value ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#39;");
}

function formatTime(v) {
  if (typeof window.formatDateTimeDisplay === "function") return window.formatDateTimeDisplay(v, "-");
  return String(v ?? "-");
}

function renderCaptureTable(rows) {
  const list = Array.isArray(rows) ? rows : [];
  const pagerApi = window.aura && typeof window.aura.paginateArray === "function" ? window.aura : null;
  const pageData = pagerApi
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
    <th>元数据</th>
    <th>图片路径</th>
  </tr>`;
  if (!pageData.rows.length) {
    captureTableBodyEl.innerHTML = `<tr><td colspan="7">暂无抓拍数据。</td></tr>`;
  } else {
    const start = (pageData.page - 1) * pageData.pageSize;
    captureTableBodyEl.innerHTML = pageData.rows
      .map((row, idx) => `<tr>
        <td class="aura-col-no">${start + idx + 1}</td>
        <td class="aura-col-id">${escapeHtml(row.captureId ?? row.CaptureId ?? "-")}</td>
        <td class="aura-col-id">${escapeHtml(row.deviceId ?? row.DeviceId ?? "-")}</td>
        <td>${escapeHtml(row.channelNo ?? row.ChannelNo ?? "-")}</td>
        <td class="aura-col-time">${escapeHtml(formatTime(row.captureTime ?? row.CaptureTime))}</td>
        <td>${escapeHtml(row.metadataJson ?? row.MetadataJson ?? "-")}</td>
        <td>${escapeHtml(row.imagePath ?? row.ImagePath ?? "-")}</td>
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
        renderCaptureTable(latestCaptureRows);
      }
    });
  }
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

async function createMock() {
  const deviceId = Number(document.getElementById("deviceId").value || 1);
  const channelNo = Number(document.getElementById("channelNo").value || 1);
  const metadataJson = document.getElementById("meta").value || "";
  setCreateCaptureResult("");

  try {
    const res = await fetch(`${apiBase}/api/capture/mock`, {
      method: "POST",
      credentials: "include",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ deviceId, channelNo, metadataJson })
    });
    const data = await res.json();
    setCreateCaptureResult(data);
    if (res.ok && data?.code === 0) {
      const deviceIdEl = document.getElementById("deviceId");
      const channelNoEl = document.getElementById("channelNo");
      const metaEl = document.getElementById("meta");
      if (deviceIdEl instanceof HTMLInputElement) deviceIdEl.value = "";
      if (channelNoEl instanceof HTMLInputElement) channelNoEl.value = "";
      if (metaEl instanceof HTMLInputElement) metaEl.value = "";
      closeCaptureCreateModal();
      capturePage = 1;
      void load();
    }
  } catch (error) {
    setCreateCaptureResult(`新增失败：${error.message}`);
  }
}

async function load() {
  setResult("");
  hideTable();
  setExportVisible(false);

  try {
    const res = await fetch(`${apiBase}/api/capture/list?limit=500`, {
      credentials: "include"
    });
    const data = await res.json();
    if (!res.ok || data?.code !== 0) {
      setResult(data);
      latestCaptureRows = [];
      setExportVisible(false);
      return;
    }
    latestCaptureRows = Array.isArray(data.data) ? data.data : [];
    renderCaptureTable(latestCaptureRows);
    setExportVisible(latestCaptureRows.length > 0);
  } catch (error) {
    setResult(`查询失败：${error.message}`);
    latestCaptureRows = [];
    setExportVisible(false);
  }
}

openCreateCaptureModalBtn?.addEventListener("click", openCaptureCreateModal);
captureCreateModalEl?.querySelectorAll("[data-aura-modal-dismiss]").forEach((el) => {
  el.addEventListener("click", () => closeCaptureCreateModal());
});

document.getElementById("load").addEventListener("click", load);
document.getElementById("create").addEventListener("click", createMock);
exportCaptureBtn?.addEventListener("click", async (event) => {
  event.preventDefault();
  event.stopPropagation();
  if (window.aura && typeof window.aura.exportDataset === "function") {
    await window.aura.exportDataset({
      apiBase,
      dataset: "capture",
      onError: (message) => setResult(message)
    });
    return;
  }
  setResult("导出失败：缺少全局导出能力");
});
void load();
