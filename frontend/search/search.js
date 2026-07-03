/* 文件：搜轨页脚本（search.js） | File: Search Script */
const apiBase = "";
const resultEl = document.getElementById("result");
const tableWrapEl = document.getElementById("tableWrap");
const searchTableBodyEl = document.getElementById("searchTableBody");
const searchPagerEl = document.getElementById("searchPager");
const searchPreviewColumn = document.getElementById("searchPreviewColumn");
const searchMainLayout = document.getElementById("searchMainLayout");
const searchPreviewImg = document.getElementById("searchPreviewImg");
const searchPreviewFileName = document.getElementById("searchPreviewFileName");
const searchResultHead = document.getElementById("searchResultHead");
const fileInputEl = document.getElementById("file");
const topKInputEl = document.getElementById("topk");
const runBtnEl = document.getElementById("runBtn");
const searchCompareModalEl = document.getElementById("searchCompareModal");
const searchCompareQueryImgEl = document.getElementById("searchCompareQueryImg");
const searchCompareHitImgEl = document.getElementById("searchCompareHitImg");
const searchCompareMetaEl = document.getElementById("searchCompareMeta");
const requestJson = window.aura?.requestJson || fallbackRequestJson;
const pageStatus = window.aura?.createStatusController?.(resultEl) || null;

let queryPreviewObjectUrl = null;
let latestSearchRows = [];
let searchPage = 1;
let searchPageSize = 15;

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
    .replaceAll("\"", "&quot;")
    .replaceAll("'", "&#39;");
}

function setResult(data, options = {}) {
  if (pageStatus && typeof pageStatus.set === "function") {
    pageStatus.set(data, options);
    return;
  }
  if (window.aura && typeof window.aura.setStatus === "function") {
    window.aura.setStatus(resultEl, data, options);
    return;
  }
  if (!resultEl) return;
  const message = typeof data === "string" ? data : String(data?.msg ?? data ?? "");
  if (!message.trim()) {
    resultEl.textContent = "";
    resultEl.hidden = true;
    resultEl.classList.remove("is-error");
    return;
  }
  resultEl.textContent = message;
  resultEl.hidden = false;
  resultEl.classList.toggle("is-error", Boolean(options.isError ?? data?.code !== 0));
}

function clearResult() {
  if (pageStatus && typeof pageStatus.clear === "function") {
    pageStatus.clear();
    return;
  }
  setResult("");
}

function setBusy(busy) {
  if (window.aura && typeof window.aura.setBusy === "function") {
    window.aura.setBusy(runBtnEl, busy);
    return;
  }
  if (runBtnEl && "disabled" in runBtnEl) runBtnEl.disabled = Boolean(busy);
}

function hideTable() {
  if (searchTableBodyEl) searchTableBodyEl.innerHTML = "";
  if (tableWrapEl) tableWrapEl.hidden = true;
  if (searchResultHead) searchResultHead.hidden = true;
  if (searchPagerEl) {
    searchPagerEl.hidden = true;
    searchPagerEl.innerHTML = "";
  }
  latestSearchRows = [];
}

function revokeQueryPreviewUrl() {
  if (queryPreviewObjectUrl) {
    URL.revokeObjectURL(queryPreviewObjectUrl);
    queryPreviewObjectUrl = null;
  }
}

function updateFilePreview() {
  revokeQueryPreviewUrl();
  const file = fileInputEl?.files?.[0];
  if (!file || !searchPreviewImg) {
    if (searchPreviewColumn) searchPreviewColumn.hidden = true;
    if (searchMainLayout) searchMainLayout.classList.add("search-main-layout--no-preview");
    if (searchPreviewImg) searchPreviewImg.removeAttribute("src");
    if (searchPreviewFileName) searchPreviewFileName.textContent = "";
    return;
  }

  queryPreviewObjectUrl = URL.createObjectURL(file);
  searchPreviewImg.src = queryPreviewObjectUrl;
  searchPreviewImg.alt = `检索用图片：${file.name}`;
  if (searchPreviewFileName) searchPreviewFileName.textContent = file.name;
  if (searchPreviewColumn) searchPreviewColumn.hidden = false;
  if (searchMainLayout) searchMainLayout.classList.remove("search-main-layout--no-preview");
}

function formatScore(value) {
  const number = Number(value);
  if (!Number.isFinite(number)) return escapeHtml(String(value ?? "-"));
  return escapeHtml(number.toFixed(4));
}

function toAbsoluteImageUrl(rawUrl) {
  const text = String(rawUrl ?? "").trim();
  if (!text) return "";
  if (/^(https?:)?\/\//i.test(text) || text.startsWith("data:") || text.startsWith("blob:")) return text;
  if (text.startsWith("/")) return `${apiBase}${text}`;
  return `${apiBase}/${text.replace(/^\.?\//, "")}`;
}

function resolveHitImageUrl(row) {
  if (!row || typeof row !== "object") return "";
  const candidateKeys = [
    "imageUrl",
    "imageURL",
    "image",
    "imagePath",
    "captureImage",
    "captureImagePath",
    "hitImageUrl",
    "hitImagePath",
    "ImageUrl",
    "ImagePath",
    "CaptureImagePath"
  ];
  for (const key of candidateKeys) {
    const value = row[key];
    if (value !== null && value !== undefined && String(value).trim() !== "") {
      return toAbsoluteImageUrl(value);
    }
  }
  return "";
}

function getQueryPreviewSrc() {
  if (!searchPreviewImg) return "";
  return String(searchPreviewImg.getAttribute("src") || "").trim();
}

function showFieldHint(message) {
  const text = String(message || "").trim();
  if (!text) return;
  if (window.aura && typeof window.aura.toast === "function") {
    window.aura.toast(text, true);
    return;
  }
  setResult({ code: 40000, msg: text }, { isError: true });
}

function openCompareModal(rowIndex) {
  if (!(searchCompareModalEl instanceof HTMLElement)) return;
  const idx = Number(rowIndex);
  if (!Number.isInteger(idx) || idx < 0 || idx >= latestSearchRows.length) return;

  const row = latestSearchRows[idx] || {};
  const vid = String(row?.vid ?? row?.Vid ?? "-").trim() || "-";
  const score = formatScore(row?.score ?? row?.Score);
  const hitImageUrl = resolveHitImageUrl(row);
  const queryImageUrl = getQueryPreviewSrc();
  if (!hitImageUrl) {
    showFieldHint("该条结果未返回命中图片，暂无可对比内容。");
    return;
  }

  if (searchCompareQueryImgEl) {
    if (queryImageUrl) {
      searchCompareQueryImgEl.src = queryImageUrl;
      searchCompareQueryImgEl.hidden = false;
    } else {
      searchCompareQueryImgEl.removeAttribute("src");
      searchCompareQueryImgEl.hidden = true;
    }
  }
  if (searchCompareHitImgEl) {
    searchCompareHitImgEl.src = hitImageUrl;
    searchCompareHitImgEl.hidden = false;
  }
  if (searchCompareMetaEl) {
    searchCompareMetaEl.textContent = `VID：${vid}，相似度：${score}`;
  }

  if (window.aura && typeof window.aura.openModal === "function") {
    window.aura.openModal(searchCompareModalEl);
    return;
  }
  searchCompareModalEl.hidden = false;
}

function closeCompareModal() {
  if (!(searchCompareModalEl instanceof HTMLElement)) return;
  if (window.aura && typeof window.aura.closeModal === "function") {
    window.aura.closeModal(searchCompareModalEl);
    return;
  }
  searchCompareModalEl.hidden = true;
}

function renderTable(rows, options = {}) {
  if (!searchTableBodyEl || !tableWrapEl) return;
  const keepPageInput = Boolean(options.keepPageInput);
  if (!keepPageInput) searchPage = 1;
  if (!Number.isFinite(searchPage) || searchPage <= 0) searchPage = 1;
  if (!Number.isFinite(searchPageSize) || searchPageSize <= 0) searchPageSize = 15;

  const list = Array.isArray(rows) ? rows : [];
  const total = list.length;
  const totalPage = Math.max(1, Math.ceil(total / searchPageSize));
  if (searchPage > totalPage) searchPage = totalPage;
  const start = (searchPage - 1) * searchPageSize;
  const pageRows = list.slice(start, start + searchPageSize);

  if (total === 0) {
    searchTableBodyEl.innerHTML = '<tr><td colspan="5">暂无相似结果，可尝试换图或调整返回条数。</td></tr>';
    tableWrapEl.hidden = false;
    if (searchResultHead) searchResultHead.hidden = false;
    if (searchPagerEl) {
      searchPagerEl.hidden = true;
      searchPagerEl.innerHTML = "";
    }
    return;
  }

  const trackTarget = ' target="_blank" rel="noopener noreferrer"';
  searchTableBodyEl.innerHTML = pageRows.map((row, index) => {
    const idx = start + index + 1;
    const vidRaw = String(row?.vid ?? row?.Vid ?? "").trim();
    const hitImageUrl = resolveHitImageUrl(row);
    const vidCell =
      vidRaw && vidRaw !== "-"
        ? `<a href="/track/?vid=${encodeURIComponent(vidRaw)}" class="aura-table-vid-link" title="新标签页打开轨迹回放"${trackTarget}>${escapeHtml(vidRaw)}</a>`
        : escapeHtml(vidRaw || "-");
    const imageCell = hitImageUrl
      ? `<img class="search-hit-thumb" src="${escapeHtml(hitImageUrl)}" alt="VID ${escapeHtml(vidRaw || "-")} 的命中图片" />`
      : '<span class="search-hit-empty">暂无命中图</span>';
    const score = row?.score ?? row?.Score;
    const compareButton = hitImageUrl
      ? `<button type="button" class="btn-secondary" data-search-action="compare" data-row-index="${idx - 1}">对比图片</button>`
      : '<button type="button" class="btn-secondary" disabled>暂无命中图</button>';
    const actionCell =
      vidRaw && vidRaw !== "-"
        ? `<div class="aura-table-actions"><a href="/track/?vid=${encodeURIComponent(vidRaw)}" class="btn-secondary"${trackTarget}>查看轨迹</a>${compareButton}</div>`
        : "-";
    return `
      <tr>
        <td class="aura-col-id">${escapeHtml(idx)}</td>
        <td>${vidCell}</td>
        <td>${imageCell}</td>
        <td>${formatScore(score)}</td>
        <td class="aura-col-action-group">${actionCell}</td>
      </tr>
    `;
  }).join("");
  tableWrapEl.hidden = false;
  if (searchResultHead) searchResultHead.hidden = false;

  if (searchPagerEl && window.aura && typeof window.aura.renderPager === "function") {
    window.aura.renderPager(searchPagerEl, {
      page: searchPage,
      pageSize: searchPageSize,
      total,
      pageSizeOptions: [15, 30, 45, 60],
      onChange: (nextPage, nextPageSize) => {
        searchPage = nextPage;
        searchPageSize = nextPageSize;
        renderTable(latestSearchRows, { keepPageInput: true });
      }
    });
  }
}

function fileToBase64(file) {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => {
      const raw = String(reader.result || "");
      const idx = raw.indexOf(",");
      resolve(idx >= 0 ? raw.slice(idx + 1) : raw);
    };
    reader.onerror = () => reject(reader.error);
    reader.readAsDataURL(file);
  });
}

function getTopK() {
  const raw = Number(topKInputEl?.value);
  return Number.isFinite(raw) && raw > 0 ? Math.min(50, Math.floor(raw)) : 10;
}

function normalizeErrorMessage(error) {
  return error instanceof Error ? error.message : String(error ?? "未知错误");
}

async function runSearch() {
  const file = fileInputEl?.files?.[0];
  const topK = getTopK();
  if (!file) {
    showFieldHint("请先选择图片");
    return;
  }

  clearResult();
  hideTable();
  setBusy(true);
  try {
    const imageBase64 = await fileToBase64(file);
    const extractResult = await requestJson(`${apiBase}/api/vector/extract`, {
      method: "POST",
      body: {
        imageBase64,
        metadataJson: JSON.stringify({ source: "search-page", fileName: file.name })
      }
    });
    const extractData = extractResult.data || {};
    if (!extractResult.ok || extractData.code !== 0) {
      hideTable();
      setResult(
        extractData?.msg
          ? extractData
          : { code: 40000, msg: `提取失败：HTTP ${extractResult.status}` },
        { isError: true }
      );
      return;
    }

    const feature = extractData?.data?.feature;
    if (!Array.isArray(feature)) {
      hideTable();
      setResult({ code: 40000, msg: "提取结果缺少特征向量" }, { isError: true });
      return;
    }

    const searchResult = await requestJson(`${apiBase}/api/vector/search`, {
      method: "POST",
      body: { feature, topK }
    });
    const searchData = searchResult.data || {};
    if (!searchResult.ok || searchData.code !== 0) {
      hideTable();
      setResult(searchData?.msg ? searchData : { code: 40000, msg: `检索失败：HTTP ${searchResult.status}` }, { isError: true });
      return;
    }

    const rows = Array.isArray(searchData.data) ? searchData.data : [];
    latestSearchRows = rows;
    renderTable(rows, { keepPageInput: false });
    const message = `检索完成：共 ${rows.length} 条结果`;
    setResult({ code: 0, msg: message });
    if (window.aura && typeof window.aura.toast === "function") {
      window.aura.toast(message, false);
    }
  } catch (error) {
    hideTable();
    setResult({ code: 40000, msg: `检索失败：${normalizeErrorMessage(error)}` }, { isError: true });
  } finally {
    setBusy(false);
  }
}

runBtnEl?.addEventListener("click", runSearch);
fileInputEl?.addEventListener("change", updateFilePreview);
window.addEventListener("beforeunload", revokeQueryPreviewUrl);
searchTableBodyEl?.addEventListener("click", (event) => {
  const target = event.target instanceof Element ? event.target.closest("[data-search-action=\"compare\"]") : null;
  if (!target) return;
  const rowIndex = Number(target.getAttribute("data-row-index"));
  openCompareModal(rowIndex);
});

if (window.aura && typeof window.aura.bindModalDismiss === "function") {
  window.aura.bindModalDismiss(searchCompareModalEl, { onClose: closeCompareModal });
} else {
  searchCompareModalEl?.addEventListener("click", (event) => {
    const element = event.target instanceof Element ? event.target : null;
    if (!element) return;
    if (element.classList.contains("aura-modal-backdrop") || element.closest("[data-aura-modal-dismiss]")) {
      closeCompareModal();
    }
  });
}
