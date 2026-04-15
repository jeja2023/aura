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
const searchCompareModalEl = document.getElementById("searchCompareModal");
const searchCompareQueryImgEl = document.getElementById("searchCompareQueryImg");
const searchCompareHitImgEl = document.getElementById("searchCompareHitImg");
const searchCompareMetaEl = document.getElementById("searchCompareMeta");

let queryPreviewObjectUrl = null;
let latestSearchRows = [];
let searchPage = 1;
let searchPageSize = 15;

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

function escapeHtml(value) {
  return String(value ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll("\"", "&quot;");
}

function formatScore(v) {
  const n = Number(v);
  if (!Number.isFinite(n)) return escapeHtml(String(v ?? "-"));
  return escapeHtml(n.toFixed(4));
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
    showFieldHint("该条结果未返回命中图片，暂无法进行图片比对。");
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
  searchCompareModalEl.hidden = false;
}

function closeCompareModal() {
  if (!(searchCompareModalEl instanceof HTMLElement)) return;
  searchCompareModalEl.hidden = true;
}

/** 参数类提示：居中 Toast，不写状态框 */
function showFieldHint(message) {
  const text = String(message || "").trim();
  if (!text) return;
  if (window.aura && typeof window.aura.toast === "function") {
    window.aura.toast(text, true);
    return;
  }
  setResult(text);
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
    searchTableBodyEl.innerHTML = "<tr><td colspan=\"5\">暂无相似结果，可尝试换图或调整返回条数。</td></tr>";
    tableWrapEl.hidden = false;
    if (searchResultHead) searchResultHead.hidden = false;
    if (searchPagerEl) {
      searchPagerEl.hidden = true;
      searchPagerEl.innerHTML = "";
    }
    return;
  }

  const trackTarget = ' target="_blank" rel="noopener noreferrer"';
  searchTableBodyEl.innerHTML = pageRows.map((row, i) => {
    const idx = start + i + 1;
    const vidRaw = String(row?.vid ?? row?.Vid ?? "").trim();
    const hitImageUrl = resolveHitImageUrl(row);
    const vidCell =
      vidRaw && vidRaw !== "-"
        ? `<a href="/track/?vid=${encodeURIComponent(vidRaw)}" class="aura-table-vid-link" title="新标签页打开轨迹回放"${trackTarget}>${escapeHtml(vidRaw)}</a>`
        : escapeHtml(vidRaw || "-");
    const imageCell = hitImageUrl
      ? `<img class="search-hit-thumb" src="${escapeHtml(hitImageUrl)}" alt="VID ${escapeHtml(vidRaw || "-")} 的命中图片" />`
      : "<span class=\"search-hit-empty\">暂无命中图</span>";
    const score = row?.score ?? row?.Score;
    const compareButton = hitImageUrl
      ? `<button type="button" class="btn-secondary" data-search-action="compare" data-row-index="${idx - 1}">对比图片</button>`
      : "<button type=\"button\" class=\"btn-secondary\" disabled>暂无命中图</button>";
    const actionCell =
      vidRaw && vidRaw !== "-"
        ? `<div class="aura-table-actions"><a href="/track/?vid=${encodeURIComponent(vidRaw)}" class="btn-secondary"${trackTarget}>查看轨迹</a>${compareButton}</div>`
        : "—";
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

function deriveMessage(data) {
  if (typeof data === "string") return data;
  if (data && typeof data === "object") {
    if (Array.isArray(data.data)) return `共 ${data.data.length} 条结果`;
    if (typeof data.msg === "string") return data.msg;
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

async function runSearch() {
  const file = document.getElementById("file").files?.[0];
  const topKRaw = Number(document.getElementById("topk").value);
  const topK = Number.isFinite(topKRaw) && topKRaw > 0 ? Math.min(50, Math.floor(topKRaw)) : 10;
  if (!file) {
    showFieldHint("请先选择图片");
    return;
  }
  setResult("");
  hideTable();
  try {
    const imageBase64 = await fileToBase64(file);
    const extRes = await fetch(`${apiBase}/api/vector/extract`, {
      method: "POST",
      credentials: "include",
      headers: {
        "Content-Type": "application/json"
      },
      body: JSON.stringify({
        imageBase64,
        metadataJson: JSON.stringify({ source: "search-page", fileName: file.name })
      })
    });
    const extData = await extRes.json();
    if (!extRes.ok || extData.code !== 0) {
      hideTable();
      setResult(extData?.msg ? extData : { code: 40000, msg: extData?.msg || `提取失败：HTTP ${extRes.status}` });
      return;
    }
    const feature = extData.data.feature;
    const seaRes = await fetch(`${apiBase}/api/vector/search`, {
      method: "POST",
      credentials: "include",
      headers: {
        "Content-Type": "application/json"
      },
      body: JSON.stringify({ feature, topK })
    });
    const seaData = await seaRes.json();
    if (!seaRes.ok || seaData.code !== 0) {
      hideTable();
      setResult(seaData);
      return;
    }

    const rows = Array.isArray(seaData.data) ? seaData.data : [];
    latestSearchRows = rows;
    renderTable(rows, { keepPageInput: false });
    if (window.aura && typeof window.aura.toast === "function") {
      window.aura.toast(`检索完成：共 ${rows.length} 条结果`, false);
    }
  } catch (error) {
    hideTable();
    setResult(`检索失败：${error.message}`);
  }
}

document.getElementById("runBtn").addEventListener("click", runSearch);
fileInputEl?.addEventListener("change", updateFilePreview);
searchTableBodyEl?.addEventListener("click", (event) => {
  const target = event.target instanceof Element ? event.target.closest("[data-search-action=\"compare\"]") : null;
  if (!target) return;
  const rowIndex = Number(target.getAttribute("data-row-index"));
  openCompareModal(rowIndex);
});
searchCompareModalEl?.addEventListener("click", (event) => {
  const element = event.target instanceof Element ? event.target : null;
  if (!element) return;
  if (element.classList.contains("aura-modal-backdrop")) {
    closeCompareModal();
    return;
  }
  if (element.closest("[data-aura-modal-dismiss]")) {
    closeCompareModal();
  }
});
