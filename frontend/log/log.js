/* 文件：日志页脚本（log.js）| File: Log Script */
const apiBase = "";
const resultEl = document.getElementById("result");
const tableWrapEl = document.getElementById("tableWrap");
const pagerEl = document.getElementById("pager");
const tableHeadEl = document.getElementById("tableHead");
const tableBodyEl = document.getElementById("tableBody");
const exportLogBtn = document.getElementById("exportLog");
let logPage = 1;
let logPageSize = 15;
let latestLogRows = [];

function setExportVisible(visible) {
  if (!exportLogBtn) return;
  if (window.aura && typeof window.aura.setElementVisible === "function") {
    window.aura.setElementVisible(exportLogBtn, visible);
    return;
  }
  exportLogBtn.hidden = !visible;
  exportLogBtn.disabled = !visible;
}

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

function hideTable() {
  if (pagerEl) {
    pagerEl.hidden = true;
    pagerEl.innerHTML = "";
  }
  if (tableHeadEl) tableHeadEl.innerHTML = "";
  if (tableBodyEl) tableBodyEl.innerHTML = "";
  if (tableWrapEl) tableWrapEl.hidden = true;
  latestLogRows = [];
  setExportVisible(false);
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
    return /失败|错误|异常|超时|拒绝|未授权|无权|禁止|非法|无效|无法|不能|不存在|已过期|已失效/i.test(message);
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

function escapeHtml(value) {
  return String(value ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll("\"", "&quot;");
}

function formatTableTime(value) {
  if (typeof window.formatDateTimeDisplay === "function") {
    return escapeHtml(window.formatDateTimeDisplay(value, "-"));
  }
  return escapeHtml(String(value ?? "-"));
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
    if (/(error|fatal|critical|exception|失败|错误|异常|告警)/i.test(level) || /(error|fatal|critical|exception|失败|错误|异常|告警)/i.test(detail)) {
      badges.push(buildLogBadge("异常", "error"));
    } else if (/(warn|warning|超时|重试|补偿|回退)/i.test(level) || /(warn|warning|超时|重试|补偿|回退)/i.test(detail)) {
      badges.push(buildLogBadge("关注", "warn"));
    }
  } else if (/(失败|错误|异常|超时|拒绝|回退|补偿失败)/i.test(haystack)) {
    badges.push(buildLogBadge("失败", "error"));
  } else if (/(重试|补偿|排队|待确认)/i.test(haystack)) {
    badges.push(buildLogBadge("处理中", "warn"));
  }

  if (/(ai|向量|feature|extract|search|upsert|cluster|vid)/i.test(haystack)) {
    badges.push(buildLogBadge("AI", "neutral"));
  }
  if (/(vector|向量|index|索引|ann|arango|milvus)/i.test(haystack)) {
    badges.push(buildLogBadge("向量", "neutral"));
  }
  if (/(retry|重试|补偿|queue|队列)/i.test(haystack)) {
    badges.push(buildLogBadge("重试", "neutral"));
  }

  return badges.join("");
}

function formatLogDetailCell(text) {
  const raw = String(text ?? "").trim();
  if (!raw) return '<span class="log-detail-empty">-</span>';
  return `<div class="log-detail-cell" title="${escapeHtml(raw)}">${escapeHtml(raw)}</div>`;
}

function renderOperationTable(rows) {
  if (!tableHeadEl || !tableBodyEl) return;
  tableHeadEl.innerHTML = `
    <tr>
      <th class="col-time">时间</th>
      <th class="col-main">操作员</th>
      <th class="col-main">动作</th>
      <th class="col-tag">标签</th>
      <th class="col-detail">详情</th>
    </tr>
  `;
  tableBodyEl.innerHTML = rows.map((row) => `
    <tr>
      <td>${formatTableTime(row.createdAt)}</td>
      <td>${escapeHtml(row.operatorName || "-")}</td>
      <td>${escapeHtml(row.action || "-")}</td>
      <td>${detectLogBadges(row, "operation") || '<span class="log-detail-empty">-</span>'}</td>
      <td>${formatLogDetailCell(row.detail)}</td>
    </tr>
  `).join("");
}

function renderSystemTable(rows) {
  if (!tableHeadEl || !tableBodyEl) return;
  tableHeadEl.innerHTML = `
    <tr>
      <th class="col-time">时间</th>
      <th class="col-main">级别</th>
      <th class="col-main">来源</th>
      <th class="col-tag">标签</th>
      <th class="col-detail">内容</th>
    </tr>
  `;
  tableBodyEl.innerHTML = rows.map((row) => `
    <tr>
      <td>${formatTableTime(row.createdAt)}</td>
      <td>${escapeHtml(row.level || "-")}</td>
      <td>${escapeHtml(row.source || "-")}</td>
      <td>${detectLogBadges(row, "system") || '<span class="log-detail-empty">-</span>'}</td>
      <td>${formatLogDetailCell(row.message)}</td>
    </tr>
  `).join("");
}

function renderTable(logType, payload) {
  const rows = Array.isArray(payload?.data) ? payload.data : [];
  latestLogRows = rows;
  setExportVisible(rows.length > 0);
  const pager = payload?.pager || {};
  if (rows.length === 0) {
    if (tableHeadEl) tableHeadEl.innerHTML = "";
    if (tableBodyEl) tableBodyEl.innerHTML = "<tr><td colspan=\"5\">暂无日志记录。</td></tr>";
    if (pagerEl) {
      pagerEl.hidden = true;
      pagerEl.innerHTML = "";
    }
    if (tableWrapEl) tableWrapEl.hidden = false;
    return;
  }
  if (logType === "system") renderSystemTable(rows);
  else renderOperationTable(rows);
  if (pagerEl && window.aura && typeof window.aura.renderPager === "function") {
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
  const logType = document.getElementById("logType").value;
  const keyword = document.getElementById("keyword").value.trim();
  if (!options.keepPageInput) {
    logPage = Number(logPage || 1);
    logPageSize = Number(logPageSize || 15);
  }
  if (!Number.isFinite(logPage) || logPage <= 0) logPage = 1;
  if (!Number.isFinite(logPageSize) || logPageSize <= 0) logPageSize = 15;
  const query = new URLSearchParams({ page: String(logPage), pageSize: String(logPageSize) });
  setResult("");
  hideTable();

  if (keyword) {
    query.set("keyword", keyword);
  }

  try {
    const endpoint = logType === "system" ? "/api/system-log/list" : "/api/operation/list";
    const res = await fetch(`${apiBase}${endpoint}?${query.toString()}`, {
      credentials: "include"
    });
    const data = await res.json();
    if (!res.ok) {
      setResult(data?.msg || "查询失败");
      return;
    }
    if (data?.pager) {
      logPage = Number(data.pager.page ?? logPage);
      logPageSize = Number(data.pager.pageSize ?? logPageSize);
    }
    if (!options.silentSuccessToast && window.aura && typeof window.aura.toast === "function") {
      window.aura.toast("查询成功");
    }
    renderTable(logType, data);
  } catch (error) {
    setResult(`查询失败：${error.message}`);
  }
}

document.getElementById("load").addEventListener("click", load);
exportLogBtn?.addEventListener("click", async (event) => {
  event.preventDefault();
  event.stopPropagation();
  if (!latestLogRows.length) return;
  const logType = String(document.getElementById("logType")?.value || "operation").toLowerCase();
  const dataset = logType === "system" ? "system" : "operation";
  const keyword = String(document.getElementById("keyword")?.value || "").trim();
  if (window.aura && typeof window.aura.exportDataset === "function") {
    await window.aura.exportDataset({
      apiBase,
      dataset,
      keyword,
      onError: (message) => setResult(message)
    });
    return;
  }
  setResult("导出失败：缺少全局导出能力");
});
void load({ silentSuccessToast: true });
