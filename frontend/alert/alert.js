/* 文件：告警页脚本（alert.js） | File: Alert Script */
const apiBase = "";
const resultEl = document.getElementById("result");
const createAlertResultEl = document.getElementById("createAlertResult");
const alertCreateModalEl = document.getElementById("alertCreateModal");
const openCreateAlertModalBtn = document.getElementById("openCreateAlertModal");
const exportAlertBtn = document.getElementById("exportAlert");
const tableWrapEl = document.getElementById("tableWrap");
const alertTableBodyEl = document.getElementById("alertTableBody");
const alertTypeKeywordEl = document.getElementById("alertTypeKeyword");
const alertDetailKeywordEl = document.getElementById("alertDetailKeyword");
const alertStartTimeEl = document.getElementById("alertStartTime");
const alertEndTimeEl = document.getElementById("alertEndTime");
const alertQuick24hBtn = document.getElementById("alertQuick24h");
const alertQuick7dBtn = document.getElementById("alertQuick7d");
const applyAlertFilterBtn = document.getElementById("applyAlertFilter");
const clearAlertFilterBtn = document.getElementById("clearAlertFilter");
const alertFilterSummaryEl = document.getElementById("alertFilterSummary");
const alertPagerEl = document.getElementById("alertPager");
const requestJson = window.aura?.requestJson || fallbackRequestJson;
const pageStatus = window.aura?.createStatusController?.(resultEl) || null;
const createStatus = window.aura?.createStatusController?.(createAlertResultEl, { successMs: 0 }) || null;

let latestAlertRows = [];
let latestAlertPager = { page: 1, pageSize: 15, total: 0 };
let alertPage = 1;
let alertPageSize = 15;

const ALERT_FILTER_STORAGE_KEY = "aura.alert.filter.v1";
const PAGE_SIZE_OPTIONS = [15, 30, 45, 60];

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

function setElementVisible(element, visible) {
  if (!element) return;
  if (window.aura && typeof window.aura.setElementVisible === "function") {
    window.aura.setElementVisible(element, visible);
    return;
  }
  element.hidden = !visible;
  if ("disabled" in element) element.disabled = !visible;
}

function setStatus(controller, element, data, options = {}) {
  if (controller && typeof controller.set === "function") {
    controller.set(data, options);
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

function setCreateAlertResult(data, options = {}) {
  setStatus(createStatus, createAlertResultEl, data, options);
}

function setExportVisible(visible) {
  setElementVisible(exportAlertBtn, visible);
}

function formatTableTime(value) {
  const formatter = window.aura?.formatDateTime || window.formatDateTimeDisplay;
  if (typeof formatter === "function") {
    return escapeHtml(formatter(value, "-"));
  }
  return escapeHtml(String(value ?? "-"));
}

function hideTable() {
  if (alertTableBodyEl) alertTableBodyEl.innerHTML = "";
  if (tableWrapEl) tableWrapEl.hidden = true;
  if (alertPagerEl) {
    alertPagerEl.hidden = true;
    alertPagerEl.innerHTML = "";
  }
}

function renderTable(rows, options = {}) {
  if (!alertTableBodyEl || !tableWrapEl) return;
  const safeRows = Array.isArray(rows) ? rows : [];
  const totalRows = Math.max(0, Number(options.totalRows ?? latestAlertPager.total) || 0);
  const activeFilter = Boolean(options.activeFilter);
  const emptyText = activeFilter ? "当前筛选条件无结果，请调整条件或清空筛选。" : "暂无告警数据。";

  if (!safeRows.length) {
    alertTableBodyEl.innerHTML = `<tr><td colspan="4">${escapeHtml(emptyText)}</td></tr>`;
    tableWrapEl.hidden = false;
    renderPager(totalRows);
    return;
  }

  alertTableBodyEl.innerHTML = safeRows.map((row) => `
    <tr>
      <td>${escapeHtml(row.alertId ?? "-")}</td>
      <td>${escapeHtml(row.alertType ?? "-")}</td>
      <td>${escapeHtml(row.detail ?? "-")}</td>
      <td>${formatTableTime(row.createdAt)}</td>
    </tr>
  `).join("");
  tableWrapEl.hidden = false;
  renderPager(totalRows);
}

function renderPager(totalRows) {
  if (!alertPagerEl) return;
  if (window.aura && typeof window.aura.renderPager === "function") {
    window.aura.renderPager(alertPagerEl, {
      page: alertPage,
      pageSize: alertPageSize,
      total: totalRows,
      pageSizeOptions: PAGE_SIZE_OPTIONS,
      onChange: (nextPage, nextPageSize) => {
        alertPage = Math.max(1, Number(nextPage) || 1);
        alertPageSize = Math.max(1, Number(nextPageSize) || PAGE_SIZE_OPTIONS[0]);
        void load({ keepPage: true, silentSuccessToast: true });
      }
    });
    return;
  }

  alertPagerEl.hidden = true;
  alertPagerEl.innerHTML = "";
}

function getFilterValue(inputEl) {
  return String(inputEl?.value ?? "").trim();
}

function parseFilterDate(value, mode) {
  const raw = String(value ?? "").trim();
  if (!raw) return null;
  const date = new Date(raw);
  if (Number.isNaN(date.getTime())) return null;
  if (mode === "end" && raw.length <= 10) {
    date.setHours(23, 59, 59, 999);
  }
  return date;
}

function toDateTimeLocalValue(date) {
  const d = new Date(date);
  const pad2 = (value) => String(value).padStart(2, "0");
  return `${d.getFullYear()}-${pad2(d.getMonth() + 1)}-${pad2(d.getDate())}T${pad2(d.getHours())}:${pad2(d.getMinutes())}`;
}

function normalizeFilterRange() {
  let startAt = parseFilterDate(alertStartTimeEl?.value, "start");
  let endAt = parseFilterDate(alertEndTimeEl?.value, "end");
  if (startAt && endAt && startAt > endAt) {
    const nextStart = endAt;
    const nextEnd = startAt;
    startAt = nextStart;
    endAt = nextEnd;
    if (alertStartTimeEl) alertStartTimeEl.value = toDateTimeLocalValue(startAt);
    if (alertEndTimeEl) alertEndTimeEl.value = toDateTimeLocalValue(endAt);
  }
  return { startAt, endAt };
}

function hasActiveFilter() {
  return Boolean(
    getFilterValue(alertTypeKeywordEl)
    || getFilterValue(alertDetailKeywordEl)
    || getFilterValue(alertStartTimeEl)
    || getFilterValue(alertEndTimeEl)
  );
}

function buildAlertListQuery() {
  const { startAt, endAt } = normalizeFilterRange();
  const query = new URLSearchParams({
    page: String(Math.max(1, Number(alertPage) || 1)),
    pageSize: String(Math.max(1, Number(alertPageSize) || PAGE_SIZE_OPTIONS[0]))
  });

  const typeKeyword = getFilterValue(alertTypeKeywordEl);
  const detailKeyword = getFilterValue(alertDetailKeywordEl);
  if (typeKeyword) query.set("typeKeyword", typeKeyword);
  if (detailKeyword) query.set("detailKeyword", detailKeyword);
  if (startAt) query.set("from", startAt.toISOString());
  if (endAt) query.set("to", endAt.toISOString());
  return query;
}

function buildAlertExportParams() {
  const query = buildAlertListQuery();
  query.delete("page");
  query.delete("pageSize");
  query.set("maxRows", "20000");
  return Object.fromEntries(query.entries());
}

function updateFilterSummary(activeFilter) {
  if (!alertFilterSummaryEl) return;
  if (!activeFilter) {
    alertFilterSummaryEl.textContent = "";
    alertFilterSummaryEl.hidden = true;
    return;
  }

  const total = Math.max(0, Number(latestAlertPager.total) || 0);
  const current = Array.isArray(latestAlertRows) ? latestAlertRows.length : 0;
  alertFilterSummaryEl.textContent = `当前筛选命中 ${total} 条，本页 ${current} 条`;
  alertFilterSummaryEl.hidden = false;
}

function applyQuickRange(hours) {
  const end = new Date();
  const start = new Date(end.getTime() - hours * 60 * 60 * 1000);
  if (alertStartTimeEl) alertStartTimeEl.value = toDateTimeLocalValue(start);
  if (alertEndTimeEl) alertEndTimeEl.value = toDateTimeLocalValue(end);
  applyFilter();
}

function applyFilter() {
  alertPage = 1;
  persistFilterState();
  void load({ silentSuccessToast: true });
}

function clearFilter() {
  if (alertTypeKeywordEl) alertTypeKeywordEl.value = "";
  if (alertDetailKeywordEl) alertDetailKeywordEl.value = "";
  if (alertStartTimeEl) alertStartTimeEl.value = "";
  if (alertEndTimeEl) alertEndTimeEl.value = "";
  applyFilter();
}

function persistFilterState() {
  try {
    const state = {
      typeKeyword: getFilterValue(alertTypeKeywordEl),
      detailKeyword: getFilterValue(alertDetailKeywordEl),
      startTime: getFilterValue(alertStartTimeEl),
      endTime: getFilterValue(alertEndTimeEl)
    };
    localStorage.setItem(ALERT_FILTER_STORAGE_KEY, JSON.stringify(state));
  } catch {
    // localStorage is optional for this page.
  }
}

function restoreFilterState() {
  try {
    const raw = localStorage.getItem(ALERT_FILTER_STORAGE_KEY);
    if (!raw) return;
    const state = JSON.parse(raw);
    if (alertTypeKeywordEl) alertTypeKeywordEl.value = String(state?.typeKeyword ?? "");
    if (alertDetailKeywordEl) alertDetailKeywordEl.value = String(state?.detailKeyword ?? "");
    if (alertStartTimeEl) alertStartTimeEl.value = String(state?.startTime ?? "");
    if (alertEndTimeEl) alertEndTimeEl.value = String(state?.endTime ?? "");
  } catch {
    // Ignore stale or invalid filter state.
  }
}

function closeAlertCreateModal() {
  if (window.aura && typeof window.aura.closeModal === "function") {
    window.aura.closeModal(alertCreateModalEl);
    return;
  }
  if (!alertCreateModalEl) return;
  alertCreateModalEl.hidden = true;
  document.body.style.overflow = "";
}

function openAlertCreateModal() {
  clearStatus(createStatus, createAlertResultEl);
  if (window.aura && typeof window.aura.openModal === "function") {
    window.aura.openModal(alertCreateModalEl, { focus: "#type", select: true });
    return;
  }
  if (!alertCreateModalEl) return;
  alertCreateModalEl.hidden = false;
  document.body.style.overflow = "hidden";
  document.getElementById("type")?.focus();
}

async function createAlert() {
  const alertType = getFilterValue(document.getElementById("type"));
  const detail = getFilterValue(document.getElementById("detail"));
  clearStatus(createStatus, createAlertResultEl);

  if (!alertType || !detail) {
    setCreateAlertResult("请填写告警类型和详情", { isError: true });
    return;
  }

  try {
    const result = await requestJson(`${apiBase}/api/alert/create`, {
      method: "POST",
      body: { alertType, detail }
    });
    const data = result.data;
    setCreateAlertResult(data || { code: result.ok ? 0 : result.status, msg: result.ok ? "创建成功" : "创建失败" });
    if (result.ok && data?.code === 0) {
      const typeEl = document.getElementById("type");
      const detailEl = document.getElementById("detail");
      if (typeEl instanceof HTMLInputElement) typeEl.value = "";
      if (detailEl instanceof HTMLInputElement) detailEl.value = "";
      closeAlertCreateModal();
      void load({ silentSuccessToast: true });
    }
  } catch (error) {
    setCreateAlertResult(`新增失败：${error.message}`, { isError: true });
  }
}

async function load(options = {}) {
  if (!options.keepPage) alertPage = 1;
  clearStatus(pageStatus, resultEl);
  hideTable();
  if (!options.keepExportState) setExportVisible(false);

  const activeFilter = hasActiveFilter();
  try {
    const query = buildAlertListQuery();
    const result = await requestJson(`${apiBase}/api/alert/list?${query.toString()}`);
    const data = result.data || {};
    if (!result.ok || data.code !== 0) {
      latestAlertRows = [];
      latestAlertPager = { page: alertPage, pageSize: alertPageSize, total: 0 };
      renderTable([], { activeFilter, totalRows: 0 });
      updateFilterSummary(activeFilter);
      setResult(data.msg ? data : `查询失败：HTTP ${result.status}`, { isError: true });
      return;
    }

    latestAlertRows = Array.isArray(data.data) ? data.data : [];
    const pager = data.pager || data.pagination || {};
    alertPage = Math.max(1, Number(pager.page ?? alertPage) || 1);
    alertPageSize = Math.max(1, Number(pager.pageSize ?? alertPageSize) || PAGE_SIZE_OPTIONS[0]);
    latestAlertPager = {
      page: alertPage,
      pageSize: alertPageSize,
      total: Math.max(0, Number(pager.total ?? latestAlertRows.length) || 0)
    };

    const totalPages = Math.max(1, Math.ceil(latestAlertPager.total / alertPageSize));
    if (latestAlertRows.length === 0 && latestAlertPager.total > 0 && alertPage > totalPages) {
      alertPage = totalPages;
      await load({ keepPage: true, silentSuccessToast: true, keepExportState: options.keepExportState });
      return;
    }

    renderTable(latestAlertRows, { activeFilter, totalRows: latestAlertPager.total });
    updateFilterSummary(activeFilter);
    setExportVisible(latestAlertPager.total > 0);
    if (!options.silentSuccessToast) {
      setResult(data);
    }
  } catch (error) {
    latestAlertRows = [];
    latestAlertPager = { page: alertPage, pageSize: alertPageSize, total: 0 };
    hideTable();
    updateFilterSummary(activeFilter);
    setExportVisible(false);
    setResult(`查询失败：${error.message}`, { isError: true });
  }
}

function handleFilterEnter(event) {
  if (event.key === "Enter") {
    applyFilter();
  }
}

openCreateAlertModalBtn?.addEventListener("click", openAlertCreateModal);
if (window.aura && typeof window.aura.bindModalDismiss === "function") {
  window.aura.bindModalDismiss(alertCreateModalEl, { onClose: closeAlertCreateModal });
} else {
  alertCreateModalEl?.querySelectorAll("[data-aura-modal-dismiss]").forEach((el) => {
    el.addEventListener("click", closeAlertCreateModal);
  });
  alertCreateModalEl?.querySelector(".aura-modal-backdrop")?.addEventListener("click", closeAlertCreateModal);
}

document.getElementById("load")?.addEventListener("click", () => load());
document.getElementById("create")?.addEventListener("click", createAlert);
applyAlertFilterBtn?.addEventListener("click", applyFilter);
clearAlertFilterBtn?.addEventListener("click", clearFilter);
alertQuick24hBtn?.addEventListener("click", () => applyQuickRange(24));
alertQuick7dBtn?.addEventListener("click", () => applyQuickRange(24 * 7));
alertTypeKeywordEl?.addEventListener("keydown", handleFilterEnter);
alertDetailKeywordEl?.addEventListener("keydown", handleFilterEnter);
alertStartTimeEl?.addEventListener("keydown", handleFilterEnter);
alertEndTimeEl?.addEventListener("keydown", handleFilterEnter);
exportAlertBtn?.addEventListener("click", async (event) => {
  event.preventDefault();
  event.stopPropagation();
  if (window.aura && typeof window.aura.exportDataset === "function") {
    await window.aura.exportDataset({
      apiBase,
      dataset: "alert",
      params: buildAlertExportParams(),
      onError: (message) => setResult(message, { isError: true })
    });
    return;
  }
  setResult("导出失败：缺少全局导出能力", { isError: true });
});

restoreFilterState();
void load({ silentSuccessToast: true });
