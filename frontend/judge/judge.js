/* 文件：研判页脚本（judge.js） | File: Judge Script */
const apiBase = "";
const resultEl = document.getElementById("result");
const exportJudgeBtn = document.getElementById("exportJudge");
const tableWrapEl = document.getElementById("tableWrap");
const judgeTableBodyEl = document.getElementById("judgeTableBody");
const judgePagerEl = document.getElementById("judgePager");
const judgeTypeFilterEl = document.getElementById("judgeTypeFilter");
const judgeVidKeywordEl = document.getElementById("judgeVidKeyword");
const judgeRoomKeywordEl = document.getElementById("judgeRoomKeyword");
const applyJudgeFilterBtn = document.getElementById("applyJudgeFilter");
const clearJudgeFilterBtn = document.getElementById("clearJudgeFilter");
const judgeFilterSummaryEl = document.getElementById("judgeFilterSummary");
let latestJudgeRows = [];
let latestFilteredJudgeRows = [];
let judgePage = 1;
let judgePageSize = 15;

function setExportVisible(visible) {
  if (!exportJudgeBtn) return;
  if (window.aura && typeof window.aura.setElementVisible === "function") {
    window.aura.setElementVisible(exportJudgeBtn, visible);
    return;
  }
  exportJudgeBtn.hidden = !visible;
  exportJudgeBtn.disabled = !visible;
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

function hideTable() {
  if (judgeTableBodyEl) judgeTableBodyEl.innerHTML = "";
  if (tableWrapEl) tableWrapEl.hidden = true;
  if (judgePagerEl) {
    judgePagerEl.hidden = true;
    judgePagerEl.innerHTML = "";
  }
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

function formatJudgeDate(v) {
  const text = String(v ?? "");
  return escapeHtml(text ? text.slice(0, 10) : "-");
}

function judgeTypeLabel(type) {
  const key = String(type ?? "");
  if (key === "home_room") return "归寝研判";
  if (key === "group_rent") return "群租研判";
  if (key === "abnormal_stay") return "异常滞留";
  if (key === "night_absence") return "夜不归宿";
  return key || "-";
}

function renderTable(rows, options = {}) {
  if (!judgeTableBodyEl || !tableWrapEl) return;
  const totalRows = Number(options.totalRows ?? 0);
  const hasActiveFilter = Boolean(options.hasActiveFilter);
  const keepPageInput = Boolean(options.keepPageInput);
  if (!keepPageInput) judgePage = 1;
  if (!Number.isFinite(judgePage) || judgePage <= 0) judgePage = 1;
  if (!Number.isFinite(judgePageSize) || judgePageSize <= 0) judgePageSize = 15;

  const sourceRows = Array.isArray(rows) ? rows : [];
  const total = sourceRows.length;
  const totalPage = Math.max(1, Math.ceil(total / judgePageSize));
  if (judgePage > totalPage) judgePage = totalPage;
  const start = (judgePage - 1) * judgePageSize;
  const pageRows = sourceRows.slice(start, start + judgePageSize);

  if (sourceRows.length === 0) {
    const emptyText = hasActiveFilter && totalRows > 0
      ? "当前筛选条件无结果，请调整筛选条件或点击“清空筛选”。"
      : "暂无研判数据。";
    judgeTableBodyEl.innerHTML = `<tr><td colspan="6">${emptyText}</td></tr>`;
    tableWrapEl.hidden = false;
    if (judgePagerEl) {
      judgePagerEl.hidden = true;
      judgePagerEl.innerHTML = "";
    }
    return;
  }

  judgeTableBodyEl.innerHTML = pageRows.map((row) => `
    <tr>
      <td>${escapeHtml(row.judgeId ?? "-")}</td>
      <td>${escapeHtml(row.vid ?? "-")}</td>
      <td>${escapeHtml(row.roomId ?? "-")}</td>
      <td>${escapeHtml(judgeTypeLabel(row.judgeType))}</td>
      <td>${formatJudgeDate(row.judgeDate)}</td>
      <td>${formatTableTime(row.createdAt)}</td>
    </tr>
  `).join("");
  tableWrapEl.hidden = false;

  if (judgePagerEl && window.aura && typeof window.aura.renderPager === "function") {
    window.aura.renderPager(judgePagerEl, {
      page: judgePage,
      pageSize: judgePageSize,
      total,
      pageSizeOptions: [15, 30, 45, 60],
      onChange: (nextPage, nextPageSize) => {
        judgePage = nextPage;
        judgePageSize = nextPageSize;
        renderTable(latestFilteredJudgeRows, {
          hasActiveFilter,
          totalRows: latestJudgeRows.length,
          keepPageInput: true
        });
      }
    });
  }
}

function getFilterValue(inputEl) {
  return String(inputEl?.value ?? "").trim().toLowerCase();
}

function getFilteredRows() {
  const typeValue = String(judgeTypeFilterEl?.value ?? "").trim().toLowerCase();
  const vidKeyword = getFilterValue(judgeVidKeywordEl);
  const roomKeyword = getFilterValue(judgeRoomKeywordEl);
  if (!typeValue && !vidKeyword && !roomKeyword) return latestJudgeRows;
  return latestJudgeRows.filter((row) => {
    const typeText = String(row?.judgeType ?? "").toLowerCase();
    const vidText = String(row?.vid ?? "").toLowerCase();
    const roomText = String(row?.roomId ?? "").toLowerCase();
    if (typeValue && typeText !== typeValue) return false;
    if (vidKeyword && !vidText.includes(vidKeyword)) return false;
    if (roomKeyword && !roomText.includes(roomKeyword)) return false;
    return true;
  });
}

function applyFilter(options = {}) {
  const filteredRows = getFilteredRows();
  latestFilteredJudgeRows = filteredRows;
  const hasActiveFilter = Boolean(
    String(judgeTypeFilterEl?.value ?? "").trim()
    || getFilterValue(judgeVidKeywordEl)
    || getFilterValue(judgeRoomKeywordEl)
  );
  renderTable(filteredRows, {
    hasActiveFilter,
    totalRows: latestJudgeRows.length,
    keepPageInput: Boolean(options.keepPageInput)
  });
  if (judgeFilterSummaryEl) {
    const total = latestJudgeRows.length;
    const hit = filteredRows.length;
    if (hasActiveFilter) {
      judgeFilterSummaryEl.textContent = `当前命中 ${hit} 条 / 总计 ${total} 条`;
      judgeFilterSummaryEl.hidden = false;
    } else {
      judgeFilterSummaryEl.textContent = "";
      judgeFilterSummaryEl.hidden = true;
    }
  }
  setExportVisible(filteredRows.length > 0);
}

function clearFilter() {
  if (judgeTypeFilterEl) judgeTypeFilterEl.value = "";
  if (judgeVidKeywordEl) judgeVidKeywordEl.value = "";
  if (judgeRoomKeywordEl) judgeRoomKeywordEl.value = "";
  applyFilter({ keepPageInput: false });
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

function getDate() {
  const val = document.getElementById("date").value;
  if (val) return val;
  const now = new Date();
  const y = now.getFullYear();
  const m = String(now.getMonth() + 1).padStart(2, "0");
  const d = String(now.getDate()).padStart(2, "0");
  return `${y}-${m}-${d}`;
}

async function post(path, body) {
  const res = await fetch(`${apiBase}${path}`, {
    method: "POST",
    credentials: "include",
    headers: {
      "Content-Type": "application/json"
    },
    body: JSON.stringify(body)
  });
  return await res.json();
}

async function load(options = {}) {
  setResult("");
  hideTable();
  if (!options.keepExportState) setExportVisible(false);

  try {
    const date = getDate();
    const res = await fetch(`${apiBase}/api/judge/daily?date=${encodeURIComponent(date)}&limit=2000`, {
      credentials: "include"
    });
    const data = await res.json();
    latestJudgeRows = Array.isArray(data?.data) ? data.data : [];
    applyFilter({ keepPageInput: false });
    if (!options.silentSuccessToast || !data || data.code !== 0) {
      setResult(data);
    }
  } catch (error) {
    setResult(`查询失败：${error.message}`);
    latestJudgeRows = [];
    hideTable();
    setExportVisible(false);
  }
}

async function runDaily() {
  setResult("");
  try {
    const date = getDate();
    const cutoffHour = Number(document.getElementById("cutoffHour").value);
    const data = await post("/api/judge/run/daily", { date, cutoffHour });
    setResult(data);
    if (data?.code === 0) void load({ silentSuccessToast: true, keepExportState: true });
  } catch (error) {
    setResult(`执行失败：${error.message}`);
  }
}

async function runHome() {
  setResult("");
  try {
    const data = await post("/api/judge/run/home", { date: getDate() });
    setResult(data);
    if (data?.code === 0) void load({ silentSuccessToast: true, keepExportState: true });
  } catch (error) {
    setResult(`执行失败：${error.message}`);
  }
}

async function runAbnormal() {
  setResult("");
  try {
    const data = await post("/api/judge/run/abnormal", {
      date: getDate(),
      groupThreshold: 2,
      stayMinutes: 120
    });
    setResult(data);
    if (data?.code === 0) void load({ silentSuccessToast: true, keepExportState: true });
  } catch (error) {
    setResult(`执行失败：${error.message}`);
  }
}

async function runNight() {
  setResult("");
  try {
    const cutoffHour = Number(document.getElementById("cutoffHour").value);
    const data = await post("/api/judge/run/night", { date: getDate(), cutoffHour });
    setResult(data);
    if (data?.code === 0) void load({ silentSuccessToast: true, keepExportState: true });
  } catch (error) {
    setResult(`执行失败：${error.message}`);
  }
}

document.getElementById("load").addEventListener("click", load);
document.getElementById("runDaily").addEventListener("click", runDaily);
document.getElementById("runHome").addEventListener("click", runHome);
document.getElementById("runAbnormal").addEventListener("click", runAbnormal);
document.getElementById("runNight").addEventListener("click", runNight);
applyJudgeFilterBtn?.addEventListener("click", () => applyFilter({ keepPageInput: false }));
clearJudgeFilterBtn?.addEventListener("click", clearFilter);
judgeTypeFilterEl?.addEventListener("change", () => applyFilter({ keepPageInput: false }));
judgeVidKeywordEl?.addEventListener("keydown", (event) => {
  if (event.key === "Enter") applyFilter({ keepPageInput: false });
});
judgeRoomKeywordEl?.addEventListener("keydown", (event) => {
  if (event.key === "Enter") applyFilter({ keepPageInput: false });
});
exportJudgeBtn?.addEventListener("click", async (event) => {
  event.preventDefault();
  event.stopPropagation();
  if (window.aura && typeof window.aura.exportDataset === "function") {
    await window.aura.exportDataset({
      apiBase,
      dataset: "judge",
      onError: (message) => setResult(message)
    });
    return;
  }
  setResult("导出失败：缺少全局导出能力");
});
void load({ silentSuccessToast: true });
