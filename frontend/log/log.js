/* 文件：日志页脚本（log.js） */
const apiBase = "";
const resultEl = document.getElementById("result");
const tableWrapEl = document.getElementById("tableWrap");
const pagerEl = document.getElementById("pager");
const tableHeadEl = document.getElementById("tableHead");
const tableBodyEl = document.getElementById("tableBody");
const exportLogBtn = document.getElementById("exportLog");
const logStartTimeEl = document.getElementById("logStartTime");
const logEndTimeEl = document.getElementById("logEndTime");
const clearLogFilterBtn = document.getElementById("clearLogFilter");
let logPage = 1;
let logPageSize = 15;
let latestLogRows = [];

const fallbackEscapeHtml = (value) => String(value ?? "")
  .replaceAll("&", "&amp;")
  .replaceAll("<", "&lt;")
  .replaceAll(">", "&gt;")
  .replaceAll('"', "&quot;")
  .replaceAll("'", "&#39;");
const escapeHtml = window.aura?.escapeHtml || fallbackEscapeHtml;
const pageStatus = window.aura?.createStatusController?.(resultEl) || null;
const requestJson = window.aura?.requestJson || fallbackRequestJson;

async function fallbackRequestJson(url, options = {}) {
  const headers = new Headers(options.headers || {});
  const init = { ...options, credentials: options.credentials || "include", headers };
  if (options.body && typeof options.body === "object" && !(options.body instanceof FormData)) {
    if (!headers.has("Content-Type")) headers.set("Content-Type", "application/json");
    init.body = JSON.stringify(options.body);
  }
  const response = await fetch(url, init);
  const text = await response.text();
  let data = null;
  try {
    data = text ? JSON.parse(text) : null;
  } catch {
    data = { code: response.ok ? 0 : -1, msg: text };
  }
  return { ok: response.ok, status: response.status, data, response };
}

function setExportVisible(visible) {
  if (!exportLogBtn) return;
  if (window.aura?.setElementVisible) {
    window.aura.setElementVisible(exportLogBtn, visible);
    return;
  }
  exportLogBtn.hidden = !visible;
  exportLogBtn.disabled = !visible;
}

function hideTable() {
  if (window.aura?.clearTable) {
    window.aura.clearTable({ wrap: tableWrapEl, head: tableHeadEl, body: tableBodyEl, pager: pagerEl });
  } else {
    if (pagerEl) {
      pagerEl.hidden = true;
      pagerEl.innerHTML = "";
    }
    if (tableHeadEl) tableHeadEl.innerHTML = "";
    if (tableBodyEl) tableBodyEl.innerHTML = "";
    if (tableWrapEl) tableWrapEl.hidden = true;
  }
  latestLogRows = [];
  setExportVisible(false);
}

function setResult(data, options = {}) {
  if (pageStatus) {
    pageStatus.set(data, options);
    return;
  }
  if (!resultEl) return;
  const text = typeof data === "string" ? data : (data?.msg || "");
  resultEl.textContent = text;
  resultEl.hidden = !text;
  resultEl.classList.toggle("is-error", Boolean(options.isError || data?.code));
}

function formatTableTime(value) {
  if (typeof window.formatDateTimeDisplay === "function") return escapeHtml(window.formatDateTimeDisplay(value, "-"));
  return escapeHtml(String(value ?? "-"));
}

function getDateTimeInput(element) {
  const text = String(element?.value ?? "").trim();
  return text || null;
}

function appendLogFilters(query) {
  const from = getDateTimeInput(logStartTimeEl);
  const to = getDateTimeInput(logEndTimeEl);
  if (from) query.set("from", from);
  if (to) query.set("to", to);
}

function buildLogExportParams() {
  const query = new URLSearchParams({ maxRows: "20000" });
  appendLogFilters(query);
  return Object.fromEntries(query.entries());
}

function clearLogFilters() {
  const keywordEl = document.getElementById("keyword");
  if (keywordEl) keywordEl.value = "";
  if (logStartTimeEl) logStartTimeEl.value = "";
  if (logEndTimeEl) logEndTimeEl.value = "";
  logPage = 1;
  void load({ silentSuccessToast: true });
}

function buildLogBadge(label, tone = "neutral") {
  return `<span class="log-badge is-${escapeHtml(tone)}">${escapeHtml(label)}</span>`;
}

function detectLogBadges(row, logType) {
  const badges = [];
  const detail = String(row?.detail ?? row?.message ?? "");
  const action = String(row?.action ?? "");
  const level = String(row?.level ?? "");
  const source = String(row?.source ?? "");
  const haystack = `${action} ${level} ${source} ${detail}`.toLowerCase();

  if (logType === "system") {
    if (/(error|fatal|critical|exception|失败|错误|告警)/i.test(level) || /(error|fatal|critical|exception|失败|错误|告警)/i.test(detail)) {
      badges.push(buildLogBadge("异常", "error"));
    } else if (/(warn|warning|超时|重试|补偿|回退)/i.test(level) || /(warn|warning|超时|重试|补偿|回退)/i.test(detail)) {
      badges.push(buildLogBadge("关注", "warn"));
    }
  } else if (/(失败|错误|超时|拒绝|补偿失败)/i.test(haystack)) {
    badges.push(buildLogBadge("失败", "error"));
  } else if (/(重试|补偿|回退|排队|待确认)/i.test(haystack)) {
    badges.push(buildLogBadge("处理中", "warn"));
  }

  if (/(ai|向量|feature|extract|search|upsert|cluster|vid)/i.test(haystack)) badges.push(buildLogBadge("AI", "neutral"));
  if (/(vector|向量|index|索引|ann|arango|milvus)/i.test(haystack)) badges.push(buildLogBadge("向量", "neutral"));
  if (/(retry|重试|补偿|queue|队列)/i.test(haystack)) badges.push(buildLogBadge("重试", "neutral"));
  return badges.join("");
}

function formatLogDetailCell(text) {
  const raw = String(text ?? "").trim();
  if (!raw) return '<span class="log-detail-empty">-</span>';
  return `<div class="log-detail-cell" title="${escapeHtml(raw)}">${escapeHtml(raw)}</div>`;
}

function operationRowHtml(row) {
  return `<tr>
    <td>${formatTableTime(row.createdAt)}</td>
    <td>${escapeHtml(row.operatorName || "-")}</td>
    <td>${escapeHtml(row.action || "-")}</td>
    <td>${detectLogBadges(row, "operation") || '<span class="log-detail-empty">-</span>'}</td>
    <td>${formatLogDetailCell(row.detail)}</td>
  </tr>`;
}

function systemRowHtml(row) {
  return `<tr>
    <td>${formatTableTime(row.createdAt)}</td>
    <td>${escapeHtml(row.level || "-")}</td>
    <td>${escapeHtml(row.source || "-")}</td>
    <td>${detectLogBadges(row, "system") || '<span class="log-detail-empty">-</span>'}</td>
    <td>${formatLogDetailCell(row.message)}</td>
  </tr>`;
}

function renderTable(logType, payload) {
  const rows = Array.isArray(payload?.data) ? payload.data : [];
  latestLogRows = rows;
  setExportVisible(rows.length > 0);
  const pager = payload?.pager || {};
  const isSystem = logType === "system";
  const columns = isSystem
    ? [
        { label: "时间", className: "col-time" },
        { label: "级别", className: "col-main" },
        { label: "来源", className: "col-main" },
        { label: "标签", className: "col-tag" },
        { label: "内容", className: "col-detail" }
      ]
    : [
        { label: "时间", className: "col-time" },
        { label: "操作员", className: "col-main" },
        { label: "动作", className: "col-main" },
        { label: "标签", className: "col-tag" },
        { label: "详情", className: "col-detail" }
      ];

  if (window.aura?.renderTable) {
    window.aura.renderTable({
      wrap: tableWrapEl,
      head: tableHeadEl,
      body: tableBodyEl,
      columns,
      rows,
      emptyText: "暂无日志记录。",
      rowHtml: isSystem ? systemRowHtml : operationRowHtml
    });
  } else if (tableHeadEl && tableBodyEl) {
    tableHeadEl.innerHTML = `<tr>${columns.map((col) => `<th class="${escapeHtml(col.className)}">${escapeHtml(col.label)}</th>`).join("")}</tr>`;
    tableBodyEl.innerHTML = rows.length ? rows.map(isSystem ? systemRowHtml : operationRowHtml).join("") : '<tr><td colspan="5">暂无日志记录。</td></tr>';
    if (tableWrapEl) tableWrapEl.hidden = false;
  }

  if (pagerEl && window.aura?.renderPager) {
    window.aura.renderPager(pagerEl, {
      page: Number(pager.page ?? logPage),
      pageSize: Number(pager.pageSize ?? logPageSize),
      total: Number(pager.total ?? rows.length),
      pageSizeOptions: [15, 30, 45, 60],
      onChange: (nextPage, nextPageSize) => {
        logPage = nextPage;
        logPageSize = nextPageSize;
        void load({ silentSuccessToast: true, keepPageInput: true });
      }
    });
  }
  if (tableWrapEl) tableWrapEl.hidden = false;
}

async function load(options = {}) {
  const logType = document.getElementById("logType")?.value || "operation";
  const keyword = String(document.getElementById("keyword")?.value || "").trim();
  if (!Number.isFinite(Number(logPage)) || Number(logPage) <= 0) logPage = 1;
  if (!Number.isFinite(Number(logPageSize)) || Number(logPageSize) <= 0) logPageSize = 15;
  const query = new URLSearchParams({ page: String(logPage), pageSize: String(logPageSize) });
  if (keyword) query.set("keyword", keyword);
  appendLogFilters(query);
  setResult("");
  hideTable();

  try {
    const endpoint = logType === "system" ? "/api/system-log/list" : "/api/operation/list";
    const result = await requestJson(`${apiBase}${endpoint}?${query.toString()}`);
    const data = result.data;
    if (!result.ok) {
      setResult(data?.msg || "查询失败", { isError: true });
      return;
    }
    if (data?.pager) {
      logPage = Number(data.pager.page ?? logPage);
      logPageSize = Number(data.pager.pageSize ?? logPageSize);
    }
    if (!options.silentSuccessToast && window.aura?.toast) window.aura.toast("查询成功");
    renderTable(logType, data);
  } catch (error) {
    setResult(`查询失败：${error.message}`, { isError: true });
  }
}

document.getElementById("load")?.addEventListener("click", () => { void load(); });
clearLogFilterBtn?.addEventListener("click", clearLogFilters);
[document.getElementById("keyword"), logStartTimeEl, logEndTimeEl].forEach((element) => {
  element?.addEventListener("keydown", (event) => {
    if (event.key !== "Enter") return;
    event.preventDefault();
    logPage = 1;
    void load({ silentSuccessToast: true });
  });
});
exportLogBtn?.addEventListener("click", async (event) => {
  event.preventDefault();
  event.stopPropagation();
  if (!latestLogRows.length) return;
  const logType = String(document.getElementById("logType")?.value || "operation").toLowerCase();
  const dataset = logType === "system" ? "system" : "operation";
  const keyword = String(document.getElementById("keyword")?.value || "").trim();
  if (window.aura?.exportDataset) {
    await window.aura.exportDataset({ apiBase, dataset, keyword, params: buildLogExportParams(), onError: (message) => setResult(message, { isError: true }) });
    return;
  }
  setResult("导出失败：缺少全局导出能力", { isError: true });
});
void load({ silentSuccessToast: true });
