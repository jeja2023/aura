/* 文件：角色页脚本（role.js） | File: Role Script */
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

function hideCreateRoleResult() {
  if (!createRoleResultEl) return;
  createRoleResultEl.textContent = "";
  createRoleResultEl.hidden = true;
  createRoleResultEl.classList.remove("is-error");
}

function setCreateRoleResult(data) {
  if (!createRoleResultEl) return;
  const isEmpty = !data || (typeof data === "string" && data.trim() === "");
  if (isEmpty) {
    hideCreateRoleResult();
    return;
  }
  const message = deriveMessage(data);
  const isError = isErrorPayload(data, message);
  createRoleResultEl.textContent = message;
  createRoleResultEl.hidden = false;
  createRoleResultEl.classList.toggle("is-error", isError);
}

function closeRoleCreateModal() {
  if (!roleCreateModalEl) return;
  roleCreateModalEl.hidden = true;
  document.body.style.overflow = "";
}

function openRoleCreateModal() {
  if (!roleCreateModalEl) return;
  roleCreateModalEl.hidden = false;
  document.body.style.overflow = "hidden";
  hideCreateRoleResult();
  const roleNameEl = document.getElementById("roleName");
  if (roleNameEl instanceof HTMLInputElement) {
    roleNameEl.focus();
    roleNameEl.select();
  }
}

function hideTable() {
  if (rolePagerEl) {
    rolePagerEl.hidden = true;
    rolePagerEl.innerHTML = "";
  }
  if (roleTableHeadEl) roleTableHeadEl.innerHTML = "";
  if (roleTableBodyEl) roleTableBodyEl.innerHTML = "";
  if (roleTableWrapEl) roleTableWrapEl.hidden = true;
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
  capture: "抓拍记录",
  scene: "三维空间态势",
  alert: "告警中心",
  judge: "归寝研判",
  track: "轨迹回放",
  search: "以图搜轨",
  stats: "统计驾驶舱",
  export: "报表导出",
  role: "角色管理",
  user: "用户管理",
  log: "操作与系统日志",
  all: "全部权限"
});

function formatRoleNameCn(value) {
  const raw = String(value ?? "").trim();
  if (!raw) return "-";
  const key = raw.toLowerCase();
  return ROLE_NAME_CN_MAP[key] || raw;
}

function formatPermissionCn(permissionJson) {
  const raw = String(permissionJson ?? "").trim();
  if (!raw) return "-";
  try {
    const arr = JSON.parse(raw);
    if (!Array.isArray(arr)) return raw;
    if (!arr.length) return "无";
    return arr
      .map((item) => {
        const key = String(item ?? "").trim().toLowerCase();
        return PERMISSION_CN_MAP[key] || String(item ?? "");
      })
      .join("、");
  } catch {
    return raw;
  }
}

function normalizeQueryText(value) {
  return String(value ?? "").trim().toLowerCase();
}

function filterRoleRows(rows) {
  const list = Array.isArray(rows) ? rows : [];
  const keyword = normalizeQueryText(keywordEl?.value);
  if (!keyword) return list;
  return list.filter((row) => {
    const roleId = String(row.roleId ?? row.role_id ?? row.RoleId ?? "");
    const roleName = String(row.roleName ?? row.role_name ?? row.RoleName ?? "");
    const permissionJson = String(row.permissionJson ?? row.permission_json ?? row.PermissionJson ?? "");
    const roleNameCn = formatRoleNameCn(roleName);
    const permissionCn = formatPermissionCn(permissionJson);
    const hitText = `${roleId} ${roleName} ${roleNameCn} ${permissionJson} ${permissionCn}`.toLowerCase();
    return hitText.includes(keyword);
  });
}

function renderRoleTable(rows) {
  const list = Array.isArray(rows) ? rows : [];
  const pagerApi = window.aura && typeof window.aura.paginateArray === "function" ? window.aura : null;
  const pageData = pagerApi
    ? pagerApi.paginateArray(list, rolePage, rolePageSize)
    : { rows: list, page: 1, pageSize: list.length || 20, total: list.length, totalPages: 1 };
  rolePage = pageData.page;
  rolePageSize = pageData.pageSize;
  if (!roleTableHeadEl || !roleTableBodyEl) return;
  roleTableHeadEl.innerHTML = `<tr>
    <th class="aura-col-no">序号</th>
    <th class="aura-col-id">角色ID</th>
    <th class="role-col-name">角色名称</th>
    <th class="role-col-permission">权限配置</th>
  </tr>`;
  if (!pageData.rows.length) {
    roleTableBodyEl.innerHTML = `<tr><td colspan="4">暂无角色数据。</td></tr>`;
  } else {
    const start = (pageData.page - 1) * pageData.pageSize;
    roleTableBodyEl.innerHTML = pageData.rows
      .map((row, idx) => {
        const roleId = row.roleId ?? row.role_id ?? row.RoleId ?? "-";
        const roleName = row.roleName ?? row.role_name ?? row.RoleName ?? "-";
        const permissionJson = row.permissionJson ?? row.permission_json ?? row.PermissionJson ?? "[]";
        const roleNameCn = formatRoleNameCn(roleName);
        const permissionCn = formatPermissionCn(permissionJson);
        return `<tr>
          <td class="aura-col-no">${start + idx + 1}</td>
          <td class="aura-col-id">${escapeHtml(roleId)}</td>
          <td class="role-col-name">${escapeHtml(roleNameCn)}</td>
          <td class="role-col-permission">${escapeHtml(permissionCn)}</td>
        </tr>`;
      })
      .join("");
  }
  if (roleTableWrapEl) roleTableWrapEl.hidden = false;
  if (rolePagerEl && window.aura && typeof window.aura.renderPager === "function") {
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
    const res = await fetch(`${apiBase}/api/role/list`, {
      credentials: "include"
    });
    const payload = await res.json();
    if (!res.ok || payload?.code !== 0) {
      setResult(payload);
      return;
    }
    latestRoleRows = Array.isArray(payload.data) ? payload.data : [];
    latestFilteredRoleRows = filterRoleRows(latestRoleRows);
    renderRoleTable(latestFilteredRoleRows);
  } catch (error) {
    setResult(`查询失败：${error.message}`);
  }
}

async function createRole() {
  setCreateRoleResult("");
  const roleName = document.getElementById("roleName").value.trim();
  const selectedPermissions = Array.from(permissionMenuEl?.querySelectorAll('input[type="checkbox"]:checked') ?? []).map((el) => el.value);
  const permissionJson = JSON.stringify(selectedPermissions);
  if (!roleName) {
    setCreateRoleResult("请输入角色名");
    return;
  }
  try {
    const res = await fetch(`${apiBase}/api/role/create`, {
      method: "POST",
      credentials: "include",
      headers: {
        "Content-Type": "application/json"
      },
      body: JSON.stringify({ roleName, permissionJson })
    });
    const payload = await res.json();
    setCreateRoleResult(payload);
    if (res.ok && payload?.code === 0) {
      const roleNameEl = document.getElementById("roleName");
      if (roleNameEl instanceof HTMLInputElement) roleNameEl.value = "";
      permissionMenuEl?.querySelectorAll('input[type="checkbox"]').forEach((el) => {
        if (el instanceof HTMLInputElement) el.checked = false;
      });
      closeRoleCreateModal();
      rolePage = 1;
      void load();
    }
  } catch (error) {
    setCreateRoleResult(`创建失败：${error.message}`);
  }
}

openCreateRoleModalBtn?.addEventListener("click", openRoleCreateModal);
roleCreateModalEl?.querySelectorAll("[data-aura-modal-dismiss]").forEach((el) => {
  el.addEventListener("click", () => closeRoleCreateModal());
});

document.getElementById("load").addEventListener("click", load);
document.getElementById("create").addEventListener("click", createRole);
keywordEl?.addEventListener("keydown", (event) => {
  if (event.key !== "Enter") return;
  event.preventDefault();
  rolePage = 1;
  void load();
});
void load();
