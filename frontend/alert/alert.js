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
let latestAlertRows = [];
let latestFilteredRows = [];
let alertPage = 1;
let alertPageSize = 15;
const ALERT_FILTER_STORAGE_KEY = "aura.alert.filter.v1";

function setExportVisible(visible) {
  if (!exportAlertBtn) return;
  if (window.aura && typeof window.aura.setElementVisible === "function") {
    window.aura.setElementVisible(exportAlertBtn, visible);
    return;
  }
  exportAlertBtn.hidden = !visible;
  exportAlertBtn.disabled = !visible;
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

function hideCreateAlertResult() {
  if (!createAlertResultEl) return;
  createAlertResultEl.textContent = "";
  createAlertResultEl.hidden = true;
  createAlertResultEl.classList.remove("is-error");
}

function escapeHtml(value) {
  return String(value ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll("\"", "&quot;");
}

function formatTableTime(v) {
  if (typeof window.formatDateTimeDisplay === "function") {
    return escapeHtml(window.formatDateTimeDisplay(v, "-"));
  }
  return escapeHtml(String(v ?? "-"));
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
  const totalRows = Number(options.totalRows ?? 0);
  const hasActiveFilter = Boolean(options.hasActiveFilter);
  const keepPageInput = Boolean(options.keepPageInput);
  if (!keepPageInput) alertPage = 1;
  if (!Number.isFinite(alertPage) || alertPage <= 0) alertPage = 1;
  if (!Number.isFinite(alertPageSize) || alertPageSize <= 0) alertPageSize = 15;
  const safeRows = Array.isArray(rows) ? rows : [];
  const total = safeRows.length;
  const totalPage = Math.max(1, Math.ceil(total / alertPageSize));
  if (alertPage > totalPage) alertPage = totalPage;
  const start = (alertPage - 1) * alertPageSize;
  const pageRows = safeRows.slice(start, start + alertPageSize);
  if (!Array.isArray(rows) || rows.length === 0) {
    const emptyText = hasActiveFilter && totalRows > 0
      ? "当前筛选条件无结果，请调整筛选条件或点击“清空筛选”。"
      : "暂无告警数据。";
    alertTableBodyEl.innerHTML = `<tr><td colspan="4">${emptyText}</td></tr>`;
    tableWrapEl.hidden = false;
    if (alertPagerEl) {
      alertPagerEl.hidden = true;
      alertPagerEl.innerHTML = "";
    }
    return;
  }
  alertTableBodyEl.innerHTML = pageRows.map((row) => `
    <tr>
      <td>${escapeHtml(row.alertId ?? "-")}</td>
      <td>${escapeHtml(row.alertType ?? "-")}</td>
      <td>${escapeHtml(row.detail ?? "-")}</td>
      <td>${formatTableTime(row.createdAt)}</td>
    </tr>
  `).join("");
  tableWrapEl.hidden = false;
  if (alertPagerEl && window.aura && typeof window.aura.renderPager === "function") {
    window.aura.renderPager(alertPagerEl, {
      page: alertPage,
      pageSize: alertPageSize,
      total,
      pageSizeOptions: [15, 30, 45, 60],
      onChange: (nextPage, nextPageSize) => {
        alertPage = nextPage;
        alertPageSize = nextPageSize;
        renderTable(latestFilteredRows, {
          hasActiveFilter,
          totalRows: latestAlertRows.length,
          keepPageInput: true
        });
      }
    });
  }
}

function getFilterValue(inputEl) {
  return String(inputEl?.value ?? "").trim().toLowerCase();
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
  const pad2 = (v) => String(v).padStart(2, "0");
  const year = d.getFullYear();
  const month = pad2(d.getMonth() + 1);
  const day = pad2(d.getDate());
  const hour = pad2(d.getHours());
  const minute = pad2(d.getMinutes());
  return `${year}-${month}-${day}T${hour}:${minute}`;
}

function applyQuickRange(hours) {
  const end = new Date();
  const start = new Date(end.getTime() - hours * 60 * 60 * 1000);
  if (alertStartTimeEl) alertStartTimeEl.value = toDateTimeLocalValue(start);
  if (alertEndTimeEl) alertEndTimeEl.value = toDateTimeLocalValue(end);
  applyFilter({ keepPageInput: false });
}

function getFilteredRows() {
  const typeKeyword = getFilterValue(alertTypeKeywordEl);
  const detailKeyword = getFilterValue(alertDetailKeywordEl);
  let startAt = parseFilterDate(alertStartTimeEl?.value, "start");
  let endAt = parseFilterDate(alertEndTimeEl?.value, "end");
  if (startAt && endAt && startAt > endAt) {
    const tmp = startAt;
    startAt = endAt;
    endAt = tmp;
    if (alertStartTimeEl) alertStartTimeEl.value = toDateTimeLocalValue(startAt);
    if (alertEndTimeEl) alertEndTimeEl.value = toDateTimeLocalValue(endAt);
  }
  if (!typeKeyword && !detailKeyword && !startAt && !endAt) {
    return latestAlertRows;
  }
  return latestAlertRows.filter((row) => {
    const typeText = String(row?.alertType ?? "").toLowerCase();
    const detailText = String(row?.detail ?? "").toLowerCase();
    const createdAtDate = new Date(String(row?.createdAt ?? ""));
    const hasValidRowTime = !Number.isNaN(createdAtDate.getTime());
    if (typeKeyword && !typeText.includes(typeKeyword)) return false;
    if (detailKeyword && !detailText.includes(detailKeyword)) return false;
    if (startAt && (!hasValidRowTime || createdAtDate < startAt)) return false;
    if (endAt && (!hasValidRowTime || createdAtDate > endAt)) return false;
    return true;
  });
}

function applyFilter(options = {}) {
  const filteredRows = getFilteredRows();
  latestFilteredRows = filteredRows;
  const hasActiveFilter = Boolean(
    getFilterValue(alertTypeKeywordEl)
    || getFilterValue(alertDetailKeywordEl)
    || String(alertStartTimeEl?.value ?? "").trim()
    || String(alertEndTimeEl?.value ?? "").trim()
  );
  renderTable(filteredRows, {
    hasActiveFilter,
    totalRows: latestAlertRows.length,
    keepPageInput: Boolean(options.keepPageInput)
  });
  if (alertFilterSummaryEl) {
    const total = latestAlertRows.length;
    const hit = filteredRows.length;
    if (hasActiveFilter) {
      alertFilterSummaryEl.textContent = `当前命中 ${hit} 条 / 总计 ${total} 条`;
      alertFilterSummaryEl.hidden = false;
    } else {
      alertFilterSummaryEl.textContent = "";
      alertFilterSummaryEl.hidden = true;
    }
  }
  setExportVisible(filteredRows.length > 0);
  persistFilterState();
}

function clearFilter() {
  if (alertTypeKeywordEl) alertTypeKeywordEl.value = "";
  if (alertDetailKeywordEl) alertDetailKeywordEl.value = "";
  if (alertStartTimeEl) alertStartTimeEl.value = "";
  if (alertEndTimeEl) alertEndTimeEl.value = "";
  applyFilter({ keepPageInput: false });
}

function persistFilterState() {
  try {
    const state = {
      typeKeyword: String(alertTypeKeywordEl?.value ?? ""),
      detailKeyword: String(alertDetailKeywordEl?.value ?? ""),
      startTime: String(alertStartTimeEl?.value ?? ""),
      endTime: String(alertEndTimeEl?.value ?? "")
    };
    localStorage.setItem(ALERT_FILTER_STORAGE_KEY, JSON.stringify(state));
  } catch {
    // 本地存储不可用时静默降级，不影响筛选主流程
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
    // 反序列化失败时忽略旧数据
  }
}

function setCreateAlertResult(data) {
  if (!createAlertResultEl) return;
  const isEmpty = !data || (typeof data === "string" && data.trim() === "");
  if (isEmpty) {
    hideCreateAlertResult();
    return;
  }
  const message = deriveMessage(data);
  const isError = isErrorPayload(data, message);
  createAlertResultEl.textContent = message;
  createAlertResultEl.hidden = false;
  createAlertResultEl.classList.toggle("is-error", isError);
}

function closeAlertCreateModal() {
  if (!alertCreateModalEl) return;
  alertCreateModalEl.hidden = true;
  document.body.style.overflow = "";
}

function openAlertCreateModal() {
  if (!alertCreateModalEl) return;
  alertCreateModalEl.hidden = false;
  document.body.style.overflow = "hidden";
  hideCreateAlertResult();
  const typeEl = document.getElementById("type");
  if (typeEl instanceof HTMLInputElement) typeEl.focus();
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
  // 输入校验/网络失败通常会直接以字符串形式返回
  if (typeof message === "string") {
    return /失败|错误|异常|超时|拒绝|未授权|无权|禁止|非法|无效|无法|不能|不存在|已过期|已失效/.test(message);
  }
  return false;
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

async function createAlert() {
  const alertType = document.getElementById("type").value.trim();
  const detail = document.getElementById("detail").value.trim();
  setCreateAlertResult("");

  if (!alertType || !detail) {
    setCreateAlertResult("请填写告警类型和详情");
    return;
  }

  try {
    const res = await fetch(`${apiBase}/api/alert/create`, {
      method: "POST",
      credentials: "include",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ alertType, detail })
    });
    const data = await res.json();
    setCreateAlertResult(data);
    if (res.ok && data?.code === 0) {
      const typeEl = document.getElementById("type");
      const detailEl = document.getElementById("detail");
      if (typeEl instanceof HTMLInputElement) typeEl.value = "";
      if (detailEl instanceof HTMLInputElement) detailEl.value = "";
      closeAlertCreateModal();
      void load();
    }
  } catch (error) {
    setCreateAlertResult(`新增失败：${error.message}`);
  }
}

async function load(options = {}) {
  setResult("");
  hideTable();
  if (!options.keepExportState) setExportVisible(false);

  try {
    const res = await fetch(`${apiBase}/api/alert/list?limit=500`, {
      credentials: "include"
    });
    const data = await res.json();
    latestAlertRows = Array.isArray(data?.data) ? data.data : [];
    applyFilter({ keepPageInput: false });
    if (!options.silentSuccessToast || !data || data.code !== 0) {
      setResult(data);
    }
  } catch (error) {
    setResult(`查询失败：${error.message}`);
    latestAlertRows = [];
    hideTable();
    setExportVisible(false);
  }
}

openCreateAlertModalBtn?.addEventListener("click", openAlertCreateModal);
alertCreateModalEl?.querySelectorAll("[data-aura-modal-dismiss]").forEach((el) => {
  el.addEventListener("click", () => closeAlertCreateModal());
});

document.getElementById("load").addEventListener("click", load);
document.getElementById("create").addEventListener("click", createAlert);
applyAlertFilterBtn?.addEventListener("click", () => applyFilter({ keepPageInput: false }));
clearAlertFilterBtn?.addEventListener("click", clearFilter);
alertQuick24hBtn?.addEventListener("click", () => applyQuickRange(24));
alertQuick7dBtn?.addEventListener("click", () => applyQuickRange(24 * 7));
alertTypeKeywordEl?.addEventListener("keydown", (event) => {
  if (event.key === "Enter") applyFilter({ keepPageInput: false });
});
alertDetailKeywordEl?.addEventListener("keydown", (event) => {
  if (event.key === "Enter") applyFilter({ keepPageInput: false });
});
alertStartTimeEl?.addEventListener("keydown", (event) => {
  if (event.key === "Enter") applyFilter({ keepPageInput: false });
});
alertEndTimeEl?.addEventListener("keydown", (event) => {
  if (event.key === "Enter") applyFilter({ keepPageInput: false });
});
exportAlertBtn?.addEventListener("click", async (event) => {
  event.preventDefault();
  event.stopPropagation();
  if (window.aura && typeof window.aura.exportDataset === "function") {
    await window.aura.exportDataset({
      apiBase,
      dataset: "alert",
      onError: (message) => setResult(message)
    });
    return;
  }
  setResult("导出失败：缺少全局导出能力");
});
restoreFilterState();
void load({ silentSuccessToast: true });
