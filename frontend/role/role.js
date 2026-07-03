/* 文件：角色页脚本（role.js） */
const apiBase = "";
const resultEl = document.getElementById("result");
const createRoleResultEl = document.getElementById("createRoleResult");
const openCreateRoleModalBtn = document.getElementById("openCreateRoleModal");
const keywordEl = document.getElementById("keyword");
const permissionMenuEl = document.getElementById("permissionMenu");
const roleCreateModalEl = document.getElementById("roleCreateModal");
const roleTableWrapEl = document.getElementById("roleTableWrap");
const rolePagerEl = document.getElementById("rolePager");
const roleTableHeadEl = document.getElementById("roleTableHead");
const roleTableBodyEl = document.getElementById("roleTableBody");
let latestRoleRows = [];
let latestFilteredRoleRows = [];
let rolePage = 1;
let rolePageSize = 15;

const fallbackEscapeHtml = (value) => String(value ?? "")
  .replaceAll("&", "&amp;")
  .replaceAll("<", "&lt;")
  .replaceAll(">", "&gt;")
  .replaceAll('"', "&quot;")
  .replaceAll("'", "&#39;");
const escapeHtml = window.aura?.escapeHtml || fallbackEscapeHtml;
const pageStatus = window.aura?.createStatusController?.(resultEl) || null;
const createStatus = window.aura?.createStatusController?.(createRoleResultEl, { successMs: 0 }) || null;
const requestJson = window.aura?.requestJson || (async (url, options = {}) => {
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
});

function setStatus(controller, element, data, options = {}) {
  if (controller) {
    controller.set(data, options);
    return;
  }
  if (!element) return;
  const text = typeof data === "string" ? data : (data?.msg || "");
  element.textContent = text;
  element.hidden = !text;
  element.classList.toggle("is-error", Boolean(options.isError || data?.code));
}

function clearStatus(controller, element) {
  if (controller) {
    controller.clear();
    return;
  }
  setStatus(null, element, "");
}

function setResult(data, options = {}) {
  setStatus(pageStatus, resultEl, data, options);
}

function setCreateRoleResult(data, options = {}) {
  setStatus(createStatus, createRoleResultEl, data, options);
}

function closeRoleCreateModal() {
  if (window.aura?.closeModal) {
    window.aura.closeModal(roleCreateModalEl);
    return;
  }
  if (!roleCreateModalEl) return;
  roleCreateModalEl.hidden = true;
  document.body.style.overflow = "";
}

function openRoleCreateModal() {
  clearStatus(createStatus, createRoleResultEl);
  if (window.aura?.openModal) {
    window.aura.openModal(roleCreateModalEl, { focus: "#roleName", select: true });
    return;
  }
  if (!roleCreateModalEl) return;
  roleCreateModalEl.hidden = false;
  document.body.style.overflow = "hidden";
  document.getElementById("roleName")?.focus();
}

function hideTable() {
  if (window.aura?.clearTable) {
    window.aura.clearTable({ wrap: roleTableWrapEl, head: roleTableHeadEl, body: roleTableBodyEl, pager: rolePagerEl });
    return;
  }
  if (rolePagerEl) {
    rolePagerEl.hidden = true;
    rolePagerEl.innerHTML = "";
  }
  if (roleTableHeadEl) roleTableHeadEl.innerHTML = "";
  if (roleTableBodyEl) roleTableBodyEl.innerHTML = "";
  if (roleTableWrapEl) roleTableWrapEl.hidden = true;
}

const ROLE_NAME_CN_MAP = Object.freeze({
  super_admin: "超级管理员",
  building_admin: "楼栋管理员",
  admin: "管理员",
  user: "普通用户"
});

const PERMISSION_CN_MAP = Object.freeze({
  campus: "集宿资源树",
  floor: "楼层图纸",
  camera: "摄像头布点",
  roi: "重点防区",
  device: "NVR 设备",
  device_diag: "设备联调",
  "device.diag": "设备诊断接口",
  "device.diagnostics": "设备诊断接口",
  media: "设备诊断接口",
  capture: "抓拍记录",
  scene: "三维空间态势",
  alert: "告警中心",
  "alert.manage": "告警操作",
  judge: "归寝研判",
  track: "轨迹回放",
  search: "以图搜轨",
  stats: "统计驾驶舱",
  export: "报表导出",
  report: "报表计划管理",
  reports: "报表计划管理",
  "report.manage": "报表计划管理",
  space: "空间能力管理",
  "space.manage": "空间能力管理",
  tenant: "多租户管理",
  tenants: "多租户管理",
  "tenant.manage": "多租户管理",
  ai: "AI 配置",
  ai_settings: "AI 配置",
  "ai.settings": "AI 配置",
  ai_platform: "AI 平台管理",
  "ai.platform": "AI 平台管理",
  role: "角色管理",
  user: "用户管理",
  log: "操作与系统日志",
  all: "全部权限"
});

function formatRoleNameCn(value) {
  const raw = String(value ?? "").trim();
  if (!raw) return "-";
  return ROLE_NAME_CN_MAP[raw.toLowerCase()] || raw;
}

function formatPermissionCn(permissionJson) {
  const raw = String(permissionJson ?? "").trim();
  if (!raw) return "-";
  try {
    const arr = JSON.parse(raw);
    if (!Array.isArray(arr)) return raw;
    if (!arr.length) return "无";
    return arr.map((item) => PERMISSION_CN_MAP[String(item ?? "").trim().toLowerCase()] || String(item ?? "")).join("、");
  } catch {
    return raw;
  }
}

function filterRoleRows(rows) {
  const keyword = String(keywordEl?.value ?? "").trim().toLowerCase();
  if (!keyword) return Array.isArray(rows) ? rows : [];
  return (Array.isArray(rows) ? rows : []).filter((row) => {
    const roleId = String(row.roleId ?? row.role_id ?? row.RoleId ?? "");
    const roleName = String(row.roleName ?? row.role_name ?? row.RoleName ?? "");
    const permissionJson = String(row.permissionJson ?? row.permission_json ?? row.PermissionJson ?? "");
    const hitText = `${roleId} ${roleName} ${formatRoleNameCn(roleName)} ${permissionJson} ${formatPermissionCn(permissionJson)}`.toLowerCase();
    return hitText.includes(keyword);
  });
}

function renderRoleTable(rows) {
  const list = Array.isArray(rows) ? rows : [];
  const pagerApi = window.aura?.paginateArray ? window.aura : null;
  const pageData = pagerApi
    ? pagerApi.paginateArray(list, rolePage, rolePageSize)
    : { rows: list, page: 1, pageSize: list.length || 20, total: list.length, totalPages: 1 };
  rolePage = pageData.page;
  rolePageSize = pageData.pageSize;
  if (!roleTableHeadEl || !roleTableBodyEl) return;

  const rowHtml = (row, idx) => {
    const roleId = row.roleId ?? row.role_id ?? row.RoleId ?? "-";
    const roleName = row.roleName ?? row.role_name ?? row.RoleName ?? "-";
    const permissionJson = row.permissionJson ?? row.permission_json ?? row.PermissionJson ?? "[]";
    return `<tr>
      <td class="aura-col-no">${(pageData.page - 1) * pageData.pageSize + idx + 1}</td>
      <td class="aura-col-id">${escapeHtml(roleId)}</td>
      <td class="role-col-name">${escapeHtml(formatRoleNameCn(roleName))}</td>
      <td class="role-col-permission">${escapeHtml(formatPermissionCn(permissionJson))}</td>
    </tr>`;
  };

  if (window.aura?.renderTable) {
    window.aura.renderTable({
      wrap: roleTableWrapEl,
      head: roleTableHeadEl,
      body: roleTableBodyEl,
      columns: [
        { label: "序号", className: "aura-col-no" },
        { label: "角色ID", className: "aura-col-id" },
        { label: "角色名称", className: "role-col-name" },
        { label: "权限配置", className: "role-col-permission" }
      ],
      rows: pageData.rows,
      emptyText: "暂无角色数据。",
      rowHtml
    });
  } else {
    roleTableHeadEl.innerHTML = `<tr><th class="aura-col-no">序号</th><th class="aura-col-id">角色ID</th><th class="role-col-name">角色名称</th><th class="role-col-permission">权限配置</th></tr>`;
    roleTableBodyEl.innerHTML = pageData.rows.length ? pageData.rows.map(rowHtml).join("") : `<tr><td colspan="4">暂无角色数据。</td></tr>`;
    if (roleTableWrapEl) roleTableWrapEl.hidden = false;
  }

  if (rolePagerEl && window.aura?.renderPager) {
    window.aura.renderPager(rolePagerEl, {
      page: pageData.page,
      pageSize: pageData.pageSize,
      total: pageData.total,
      pageSizeOptions: [15, 30, 45, 60],
      onChange: (nextPage, nextPageSize) => {
        rolePage = nextPage;
        rolePageSize = nextPageSize;
        renderRoleTable(latestFilteredRoleRows);
      }
    });
  }
}

async function load() {
  setResult("");
  hideTable();
  try {
    const result = await requestJson(`${apiBase}/api/role/list`);
    const payload = result.data;
    if (!result.ok || payload?.code !== 0) {
      setResult(payload || "查询失败", { isError: true });
      return;
    }
    latestRoleRows = Array.isArray(payload.data) ? payload.data : [];
    latestFilteredRoleRows = filterRoleRows(latestRoleRows);
    renderRoleTable(latestFilteredRoleRows);
  } catch (error) {
    setResult(`查询失败：${error.message}`, { isError: true });
  }
}

async function createRole() {
  setCreateRoleResult("");
  const formValues = window.aura?.readForm ? window.aura.readForm(roleCreateModalEl, { checkboxMode: "array" }) : {};
  const roleName = String(formValues.roleName ?? document.getElementById("roleName")?.value ?? "").trim();
  const selectedPermissions = Array.isArray(formValues.permissions)
    ? formValues.permissions
    : Array.from(permissionMenuEl?.querySelectorAll('input[type="checkbox"]:checked') ?? []).map((el) => el.value);
  if (!roleName) {
    setCreateRoleResult("请输入角色名", { isError: true });
    return;
  }
  try {
    const result = await requestJson(`${apiBase}/api/role/create`, {
      method: "POST",
      body: { roleName, permissionJson: JSON.stringify(selectedPermissions) }
    });
    const payload = result.data;
    setCreateRoleResult(payload || (result.ok ? "创建成功" : "创建失败"), { isError: !result.ok || payload?.code !== 0 });
    if (result.ok && payload?.code === 0) {
      if (window.aura?.resetForm) {
        window.aura.resetForm(roleCreateModalEl);
      } else {
        const roleNameEl = document.getElementById("roleName");
        if (roleNameEl instanceof HTMLInputElement) roleNameEl.value = "";
        permissionMenuEl?.querySelectorAll('input[type="checkbox"]').forEach((el) => {
          if (el instanceof HTMLInputElement) el.checked = false;
        });
      }
      closeRoleCreateModal();
      rolePage = 1;
      void load();
    }
  } catch (error) {
    setCreateRoleResult(`创建失败：${error.message}`, { isError: true });
  }
}

openCreateRoleModalBtn?.addEventListener("click", openRoleCreateModal);
if (window.aura?.bindModalDismiss) {
  window.aura.bindModalDismiss(roleCreateModalEl, { onClose: closeRoleCreateModal });
} else {
  roleCreateModalEl?.querySelectorAll("[data-aura-modal-dismiss]").forEach((el) => el.addEventListener("click", closeRoleCreateModal));
}
document.getElementById("load")?.addEventListener("click", load);
document.getElementById("create")?.addEventListener("click", createRole);
keywordEl?.addEventListener("keydown", (event) => {
  if (event.key !== "Enter") return;
  event.preventDefault();
  rolePage = 1;
  latestFilteredRoleRows = filterRoleRows(latestRoleRows);
  renderRoleTable(latestFilteredRoleRows);
});
void load();
