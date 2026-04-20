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
/** 当前登录角色（来自 /api/auth/me 的 data.role，如 super_admin） */
let currentUserRole = null;
let currentDeviceView = "manage";

const deviceManagePanelEl = document.getElementById("deviceManagePanel");
const deviceHikIsapiPanelEl = document.getElementById("deviceHikIsapiPanel");
const diagVendorSelectEl = document.getElementById("diagVendorSelect");

const VENDOR_ACTIONS_MAP = Object.freeze({
  "hik-isapi": "auraHikIsapiActions",
  "dahua-isapi": "auraDahuaIsapiActions",
  "onvif-common": "auraOnvifCommonActions"
});

function getDiagVendorsRegistry() {
  const api = window.auraDeviceDiagVendors;
  if (api && typeof api === "object") return api;
  return null;
}

function getVendorActionsApi(vendorKey) {
  const key = String(vendorKey || "").trim().toLowerCase();
  const globalName = VENDOR_ACTIONS_MAP[key];
  if (!globalName) return null;
  const api = window[globalName];
  if (api && typeof api === "object") return api;
  return null;
}

function canAccessHikIsapiDiagnostics(role) {
  const r = String(role ?? "").trim().toLowerCase();
  return r === "super_admin" || r === "building_admin";
}

function getRequestedDeviceView() {
  const pathname = String(window.location.pathname || "").replace(/\/+$/, "").toLowerCase();
  if (pathname === "/device-diag") return "diag";
  if (pathname === "/device") return "manage";
  const p = new URLSearchParams(window.location.search || "");
  const tab = String(p.get("tab") || "").trim().toLowerCase();
  return tab === "diag" || tab === "hik" ? "diag" : "manage";
}

function updatePageShellTitle(view) {
  const title = view === "diag" ? "设备联调" : "NVR 设备管理";
  if (document.body) document.body.setAttribute("data-shell-title", title);
  const titleEl = document.querySelector(".app-page-title");
  if (titleEl) titleEl.textContent = title;
}

function switchDeviceView(view) {
  currentDeviceView = view === "diag" ? "diag" : "manage";
  const showManage = currentDeviceView === "manage";
  if (deviceManagePanelEl) deviceManagePanelEl.hidden = !showManage;
  if (deviceHikIsapiPanelEl) deviceHikIsapiPanelEl.hidden = showManage;
  updatePageShellTitle(currentDeviceView);
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
  const showDiagAction = canAccessHikIsapiDiagnostics(currentUserRole);
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
    ${showDiagAction ? '<th class="aura-col-action">操作</th>' : ""}
  </tr>`;
  if (!pageData.rows.length) {
    const colspan = showDiagAction ? 10 : 9;
    deviceTableBodyEl.innerHTML = `<tr><td colspan="${colspan}">暂无设备数据。</td></tr>`;
  } else {
    const start = (pageData.page - 1) * pageData.pageSize;
    deviceTableBodyEl.innerHTML = pageData.rows
      .map((row, idx) => {
        const deviceId = row.deviceId ?? row.DeviceId ?? "";
        const deviceIdText = escapeHtml(deviceId || "-");
        return `<tr data-device-id="${escapeHtml(deviceId)}">
        <td class="aura-col-no">${start + idx + 1}</td>
        <td class="aura-col-id">${deviceIdText}</td>
        <td>${escapeHtml(row.name ?? row.Name ?? "-")}</td>
        <td>${escapeHtml(row.ip ?? row.Ip ?? "-")}</td>
        <td>${escapeHtml(row.port ?? row.Port ?? "-")}</td>
        <td>${escapeHtml(row.brand ?? row.Brand ?? "-")}</td>
        <td>${escapeHtml(row.protocol ?? row.Protocol ?? "-")}</td>
        <td>${escapeHtml(row.status ?? row.Status ?? "-")}</td>
        <td class="aura-col-time">${escapeHtml(formatTime(row.createdAt ?? row.CreatedAt))}</td>
        ${
          showDiagAction
            ? `<td class="aura-col-action-group">
              <div class="aura-table-actions" role="group" aria-label="设备操作">
                <button type="button" class="btn-secondary" data-device-action="open-hik-diag">诊断/联调</button>
              </div>
            </td>`
            : ""
        }
      </tr>`;
      })
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
    getCurrentVendorActionsApi()?.populateDeviceSelect?.(latestDeviceRows);
  } catch (error) {
    setResult(`查询失败：${error.message}`);
  }
}

async function fetchCurrentUserRole() {
  try {
    const res = await fetch(`${apiBase}/api/auth/me`, { credentials: "include" });
    if (!res.ok) return;
    const j = await res.json();
    if (j && j.code === 0 && j.data) currentUserRole = j.data.role ?? null;
  } catch {
    /* 未登录或网络异常时不阻塞页面 */
  }
}

function updateRegisterButtonVisibility() {
  const btn = document.getElementById("openDeviceRegisterModal");
  if (btn) btn.hidden = currentUserRole !== "super_admin";
}

function updateHikIsapiTabVisibility() {
  const allowed = canAccessHikIsapiDiagnostics(currentUserRole);
  const requestView = getRequestedDeviceView();
  if (requestView === "diag" && !allowed) {
    switchDeviceView("manage");
    setResult("当前账号无设备联调权限。");
    return;
  }
  switchDeviceView(requestView);
}

function updateHikIsapiGatewayVisibility() {
  const superAdminOnly = String(currentUserRole ?? "").trim().toLowerCase() === "super_admin";
  document.querySelectorAll("[data-hik-gateway-super-admin]").forEach((el) => {
    if (el instanceof HTMLElement) el.hidden = !superAdminOnly;
  });
  if (!superAdminOnly) closeHikGatewayModal();
}

function setHikDeviceIdInForm(deviceId) {
  getCurrentVendorActionsApi()?.setDeviceIdInForm?.(deviceId);
}

function applyDevicePageEntryFromQuery() {
  const p = new URLSearchParams(window.location.search || "");
  const deviceIdRaw = String(p.get("deviceId") || "").trim();
  const deviceId = deviceIdRaw ? Number(deviceIdRaw) : 0;
  if (Number.isFinite(deviceId) && deviceId > 0) setHikDeviceIdInForm(deviceId);
}

function ensureDiagVendorSelection() {
  if (!(diagVendorSelectEl instanceof HTMLSelectElement)) return;
  const vendors = getDiagVendorsRegistry();
  if (vendors && typeof vendors.renderOptions === "function") {
    vendors.renderOptions(diagVendorSelectEl);
    return;
  }
  const current = String(diagVendorSelectEl.value || "").trim().toLowerCase();
  if (!current || current === "hik-isapi") return;
  diagVendorSelectEl.value = "hik-isapi";
}

function getCurrentDiagVendorKey() {
  if (!(diagVendorSelectEl instanceof HTMLSelectElement)) return "hik-isapi";
  const raw = String(diagVendorSelectEl.value || "").trim().toLowerCase();
  const vendors = getDiagVendorsRegistry();
  if (vendors && typeof vendors.normalizeVendorKey === "function") {
    return vendors.normalizeVendorKey(raw);
  }
  return raw || "hik-isapi";
}

function getCurrentVendorActionsApi() {
  return getVendorActionsApi(getCurrentDiagVendorKey()) || getVendorActionsApi("hik-isapi");
}

function showDiagVendorUnavailableHint(vendorKey) {
  const vendors = getDiagVendorsRegistry();
  if (vendors && typeof vendors.getUnavailableHint === "function") {
    const hint = vendors.getUnavailableHint(vendorKey);
    if (hint) getCurrentVendorActionsApi()?.setDisplay?.(hint);
  }
}

function ensureCurrentDiagVendorEnabled() {
  const vendorKey = getCurrentDiagVendorKey();
  const vendors = getDiagVendorsRegistry();
  if (!vendors || typeof vendors.isVendorEnabled !== "function") return true;
  const enabled = vendors.isVendorEnabled(vendorKey);
  if (enabled) return true;
  const fallback =
    typeof vendors.getDefaultVendorKey === "function" ? vendors.getDefaultVendorKey() : "hik-isapi";
  if (diagVendorSelectEl instanceof HTMLSelectElement) {
    diagVendorSelectEl.value = fallback;
  }
  showDiagVendorUnavailableHint(vendorKey);
  return false;
}

const deviceRegisterModalEl = document.getElementById("deviceRegisterModal");
const deviceRegisterResultEl = document.getElementById("deviceRegisterResult");
const hikGatewayModalEl = document.getElementById("hikGatewayModal");
const deviceOnboardModalEl = document.getElementById("deviceOnboardModal");

function hideDeviceRegisterResult() {
  if (!deviceRegisterResultEl) return;
  deviceRegisterResultEl.textContent = "";
  deviceRegisterResultEl.hidden = true;
  deviceRegisterResultEl.classList.remove("is-error");
}

function setDeviceRegisterResult(message, isError) {
  if (!deviceRegisterResultEl) return;
  deviceRegisterResultEl.textContent = message;
  deviceRegisterResultEl.hidden = false;
  deviceRegisterResultEl.classList.toggle("is-error", Boolean(isError));
}

function openDeviceRegisterModal() {
  if (currentUserRole !== "super_admin") return;
  if (!deviceRegisterModalEl) return;
  const n = document.getElementById("regName");
  const ip = document.getElementById("regIp");
  const port = document.getElementById("regPort");
  const brand = document.getElementById("regBrand");
  const protocol = document.getElementById("regProtocol");
  if (n instanceof HTMLInputElement) n.value = "";
  if (ip instanceof HTMLInputElement) ip.value = "";
  if (port instanceof HTMLInputElement) port.value = "80";
  if (brand instanceof HTMLInputElement) brand.value = "海康威视";
  if (protocol instanceof HTMLSelectElement) protocol.value = "HIK-ISAPI";
  deviceRegisterModalEl.hidden = false;
  document.body.style.overflow = "hidden";
  hideDeviceRegisterResult();
  n?.focus();
}

function closeDeviceRegisterModal() {
  if (!deviceRegisterModalEl) return;
  deviceRegisterModalEl.hidden = true;
  document.body.style.overflow = "";
}

function openHikGatewayModal() {
  if (String(currentUserRole ?? "").trim().toLowerCase() !== "super_admin") return;
  if (!hikGatewayModalEl) return;
  hikGatewayModalEl.hidden = false;
  document.body.style.overflow = "hidden";
}

function closeHikGatewayModal() {
  if (!hikGatewayModalEl) return;
  hikGatewayModalEl.hidden = true;
  document.body.style.overflow = "";
}

function getCapturePushUrl() {
  const origin = window.location.origin;
  return `${origin}/api/capture/push`;
}

function hydrateCapturePushUrlInline() {
  const el = document.getElementById("capturePushUrlInline");
  if (!el) return;
  el.textContent = getCapturePushUrl();
}

function openDeviceOnboardModal() {
  if (!deviceOnboardModalEl) return;
  hydrateCapturePushUrlInline();
  deviceOnboardModalEl.hidden = false;
  document.body.style.overflow = "hidden";
}

function closeDeviceOnboardModal() {
  if (!deviceOnboardModalEl) return;
  deviceOnboardModalEl.hidden = true;
  document.body.style.overflow = "";
}

async function submitDeviceRegister() {
  hideDeviceRegisterResult();
  const name = document.getElementById("regName")?.value?.trim() ?? "";
  const ip = document.getElementById("regIp")?.value?.trim() ?? "";
  const port = Number(document.getElementById("regPort")?.value ?? 80);
  const brand = document.getElementById("regBrand")?.value?.trim() || "海康威视";
  const protocol = document.getElementById("regProtocol")?.value || "HIK-ISAPI";
  if (!name || !ip) {
    setDeviceRegisterResult("请填写设备名称与 IP。", true);
    return;
  }
  if (!Number.isFinite(port) || port < 1 || port > 65535) {
    setDeviceRegisterResult("端口须为 1～65535。", true);
    return;
  }
  try {
    const res = await fetch(`${apiBase}/api/device/register`, {
      method: "POST",
      credentials: "include",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ name, ip, port, brand, protocol })
    });
    let data;
    try {
      data = await res.json();
    } catch {
      setDeviceRegisterResult(`注册失败：响应不是合法 JSON（HTTP ${res.status}）`, true);
      return;
    }
    const ok = res.ok && data?.code === 0;
    if (ok) {
      closeDeviceRegisterModal();
      setResult(data);
      await load();
      return;
    }
    const msg =
      res.status === 403 ? "仅超级管理员可注册设备。" : typeof data?.msg === "string" ? data.msg : `HTTP ${res.status}`;
    setDeviceRegisterResult(msg, true);
  } catch (error) {
    setDeviceRegisterResult(`提交失败：${error.message}`, true);
  }
}

async function bootstrapDevicePage() {
  switchDeviceView("manage");
  ensureDiagVendorSelection();
  Object.keys(VENDOR_ACTIONS_MAP).forEach((key) => {
    getVendorActionsApi(key)?.init?.({
      apiBase,
      canRunVendorAction: () => getCurrentDiagVendorKey() === key && ensureCurrentDiagVendorEnabled()
    });
  });
  await fetchCurrentUserRole();
  updateRegisterButtonVisibility();
  updateHikIsapiTabVisibility();
  updateHikIsapiGatewayVisibility();
  await load();
  applyDevicePageEntryFromQuery();
  void getCurrentVendorActionsApi()?.startSignalR?.();
}

document.getElementById("load").addEventListener("click", load);
document.getElementById("openDeviceRegisterModal")?.addEventListener("click", openDeviceRegisterModal);
document.getElementById("openHikGatewayModal")?.addEventListener("click", openHikGatewayModal);
document.querySelectorAll("#openDeviceOnboardModal").forEach((el) => {
  el.addEventListener("click", openDeviceOnboardModal);
});
document.getElementById("deviceRegisterSubmit")?.addEventListener("click", submitDeviceRegister);
deviceRegisterModalEl?.querySelectorAll("[data-aura-modal-dismiss]").forEach((el) => {
  el.addEventListener("click", () => closeDeviceRegisterModal());
});
hikGatewayModalEl?.querySelectorAll("[data-hik-gateway-dismiss]").forEach((el) => {
  el.addEventListener("click", () => closeHikGatewayModal());
});
deviceOnboardModalEl?.querySelectorAll("[data-device-onboard-dismiss]").forEach((el) => {
  el.addEventListener("click", () => closeDeviceOnboardModal());
});
diagVendorSelectEl?.addEventListener("change", () => {
  const selected = getCurrentDiagVendorKey();
  const enabled = ensureCurrentDiagVendorEnabled();
  if (!enabled) return;
  getCurrentVendorActionsApi()?.setDisplay?.(`已切换到 ${selected} 诊断能力。`);
});

deviceTableBodyEl?.addEventListener("click", (event) => {
  const target = event.target;
  if (!(target instanceof HTMLElement)) return;
  const btn = target.closest("button[data-device-action]");
  if (!(btn instanceof HTMLElement)) return;
  const action = String(btn.dataset.deviceAction || "").trim();
  if (action !== "open-hik-diag") return;
  if (!canAccessHikIsapiDiagnostics(currentUserRole)) return;
  const tr = btn.closest("tr[data-device-id]");
  const deviceIdRaw = tr instanceof HTMLElement ? String(tr.dataset.deviceId || "") : "";
  const deviceId = deviceIdRaw ? Number(deviceIdRaw) : 0;
  if (!Number.isFinite(deviceId) || deviceId <= 0) return;
  const next = new URL(window.location.href);
  next.pathname = "/device-diag/";
  next.searchParams.delete("tab");
  next.searchParams.set("deviceId", String(deviceId));
  window.location.href = next.toString();
});

void bootstrapDevicePage();
