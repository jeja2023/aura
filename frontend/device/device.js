/* 文件：设备页脚本（device.js） | File: Device Script */
const apiBase = "";
const resultEl = document.getElementById("result");
const deviceTableWrapEl = document.getElementById("deviceTableWrap");
const devicePagerEl = document.getElementById("devicePager");
const deviceTableHeadEl = document.getElementById("deviceTableHead");
const deviceTableBodyEl = document.getElementById("deviceTableBody");
let latestDeviceRows = [];
let devicePage = 1;
let devicePageSize = 15;

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
  if (devicePagerEl) {
    devicePagerEl.hidden = true;
    devicePagerEl.innerHTML = "";
  }
  if (deviceTableHeadEl) deviceTableHeadEl.innerHTML = "";
  if (deviceTableBodyEl) deviceTableBodyEl.innerHTML = "";
  if (deviceTableWrapEl) deviceTableWrapEl.hidden = true;
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

function renderDeviceTable(rows) {
  const list = Array.isArray(rows) ? rows : [];
  const pagerApi = window.aura && typeof window.aura.paginateArray === "function" ? window.aura : null;
  const pageData = pagerApi
    ? pagerApi.paginateArray(list, devicePage, devicePageSize)
    : { rows: list, page: 1, pageSize: list.length || 20, total: list.length, totalPages: 1 };
  devicePage = pageData.page;
  devicePageSize = pageData.pageSize;
  if (!deviceTableHeadEl || !deviceTableBodyEl) return;
  deviceTableHeadEl.innerHTML = `<tr>
    <th class="aura-col-no">序号</th>
    <th class="aura-col-id">设备ID</th>
    <th>名称</th>
    <th>IP</th>
    <th>端口</th>
    <th>品牌</th>
    <th>协议</th>
    <th>状态</th>
    <th class="aura-col-time">创建时间</th>
  </tr>`;
  if (!pageData.rows.length) {
    deviceTableBodyEl.innerHTML = `<tr><td colspan="9">暂无设备数据。</td></tr>`;
  } else {
    const start = (pageData.page - 1) * pageData.pageSize;
    deviceTableBodyEl.innerHTML = pageData.rows
      .map((row, idx) => `<tr>
        <td class="aura-col-no">${start + idx + 1}</td>
        <td class="aura-col-id">${escapeHtml(row.deviceId ?? row.DeviceId ?? "-")}</td>
        <td>${escapeHtml(row.name ?? row.Name ?? "-")}</td>
        <td>${escapeHtml(row.ip ?? row.Ip ?? "-")}</td>
        <td>${escapeHtml(row.port ?? row.Port ?? "-")}</td>
        <td>${escapeHtml(row.brand ?? row.Brand ?? "-")}</td>
        <td>${escapeHtml(row.protocol ?? row.Protocol ?? "-")}</td>
        <td>${escapeHtml(row.status ?? row.Status ?? "-")}</td>
        <td class="aura-col-time">${escapeHtml(formatTime(row.createdAt ?? row.CreatedAt))}</td>
      </tr>`)
      .join("");
  }
  if (deviceTableWrapEl) deviceTableWrapEl.hidden = false;
  if (devicePagerEl && window.aura && typeof window.aura.renderPager === "function") {
    window.aura.renderPager(devicePagerEl, {
      page: pageData.page,
      pageSize: pageData.pageSize,
      total: pageData.total,
      pageSizeOptions: [15, 30, 45, 60],
      onChange: (nextPage, nextPageSize) => {
        devicePage = nextPage;
        devicePageSize = nextPageSize;
        renderDeviceTable(latestDeviceRows);
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

async function load() {
  setResult("");
  hideTable();

  try {
    const res = await fetch(`${apiBase}/api/device/list`, {
      credentials: "include"
    });
    const data = await res.json();
    if (!res.ok || data?.code !== 0) {
      setResult(data);
      return;
    }
    latestDeviceRows = Array.isArray(data.data) ? data.data : [];
    renderDeviceTable(latestDeviceRows);
  } catch (error) {
    setResult(`查询失败：${error.message}`);
  }
}

document.getElementById("load").addEventListener("click", load);
void load();
