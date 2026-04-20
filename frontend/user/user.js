/* 文件：用户页脚本（user.js） | File: User Script */
const apiBase = "";
const createResultEl = document.getElementById("createResult");
const queryResultEl = document.getElementById("queryResult");
const userListEl = document.getElementById("userList");
const userListMetaEl = document.getElementById("userListMeta");
const userPagerEl = document.getElementById("userPager");
const userKeywordEl = document.getElementById("userKeyword");
const exportUsersBtn = document.getElementById("exportUsers");
const importFileEl = document.getElementById("importFile");
const modalCreate = document.getElementById("userModalCreate");
const modalResetPassword = document.getElementById("userModalResetPassword");
const modalDelete = document.getElementById("userModalDelete");

const resetUserIdEl = document.getElementById("resetUserId");
const resetPasswordResultEl = document.getElementById("resetPasswordResult");
const confirmResetPasswordBtn = document.getElementById("confirmResetPassword");

const deleteUserIdEl = document.getElementById("deleteUserId");
const deleteUserIdDisplayEl = document.getElementById("deleteUserIdDisplay");
const deleteResultEl = document.getElementById("deleteResult");
const confirmDeleteUserBtn = document.getElementById("confirmDeleteUser");
let latestUserPayload = null;
const DEFAULT_USER_PAGE_SIZE = 15;
let userPage = 1;
let userPageSize = DEFAULT_USER_PAGE_SIZE;

function showToast(message, isError = false) {
  const text = String(message ?? "").trim();
  if (!text) return;
  if (window.aura && typeof window.aura.toast === "function") {
    window.aura.toast(text, isError);
  }
}

function setExportVisible(visible) {
  if (!exportUsersBtn) return;
  if (window.aura && typeof window.aura.setElementVisible === "function") {
    window.aura.setElementVisible(exportUsersBtn, visible);
    return;
  }
  exportUsersBtn.hidden = !visible;
  exportUsersBtn.disabled = !visible;
}

/** 成功提示自动消失定时器（跨页面仅保留一个，最后一次触发的元素会自动消失） */
let successStatusTimer = null;
let successTargetEl = null;
const SUCCESS_STATUS_MS = 5000;

function escapeHtml(text) {
  return String(text ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#39;");
}

/** 列表「创建时间」列展示 */
function formatCreatedAtCell(v) {
  if (typeof window.formatDateTimeDisplay === "function") {
    return escapeHtml(window.formatDateTimeDisplay(v, "—"));
  }
  if (v == null || v === "") return "—";
  const d = new Date(v);
  if (Number.isNaN(d.getTime())) return escapeHtml(String(v).replace("T", " "));
  return escapeHtml(
    d.toLocaleString("zh-CN", {
      year: "numeric",
      month: "2-digit",
      day: "2-digit",
      hour: "2-digit",
      minute: "2-digit",
      second: "2-digit",
      hour12: false
    })
  );
}

function clearSuccessStatusTimer() {
  if (successStatusTimer != null) {
    clearTimeout(successStatusTimer);
    successStatusTimer = null;
  }
  successTargetEl = null;
}

function hideElResult(el) {
  if (!el) return;
  el.textContent = "";
  el.hidden = true;
  el.classList.remove("is-error");
}

function deriveElMessage(data) {
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

function shouldAutoHideSuccess(_message) {
  return true;
}

function toCsvCell(v) {
  const text = String(v ?? "");
  if (/[",\r\n]/.test(text)) return `"${text.replaceAll('"', '""')}"`;
  return text;
}

/** 按优先级取表头列下标（先匹配靠前的候选名） */
function pickCsvColumnIndex(header, candidates) {
  for (const name of candidates) {
    const i = header.indexOf(name);
    if (i >= 0) return i;
  }
  return -1;
}

function parseCsvLine(line) {
  const out = [];
  let current = "";
  let inQuotes = false;
  for (let i = 0; i < line.length; i += 1) {
    const ch = line[i];
    if (ch === '"') {
      const next = line[i + 1];
      if (inQuotes && next === '"') {
        current += '"';
        i += 1;
      } else {
        inQuotes = !inQuotes;
      }
      continue;
    }
    if (ch === "," && !inQuotes) {
      out.push(current);
      current = "";
      continue;
    }
    current += ch;
  }
  out.push(current);
  return out.map((x) => x.trim());
}

/** 根据导入表中的「角色」列（展示名）解析为接口所需的 roleId（1=管理员，2=普通用户）。 */
function normalizeImportRoleNameToId(text) {
  const raw = String(text ?? "").trim();
  if (!raw) return 2;
  if (raw === "1" || raw === "2") return Number(raw);
  const v = raw.toLowerCase();
  if (
    v === "管理员" ||
    v === "超级管理员" ||
    v === "super_admin" ||
    v === "admin" ||
    v === "administrator"
  ) {
    return 1;
  }
  if (
    v === "普通用户" ||
    v === "楼栋管理员" ||
    v === "building_admin" ||
    v === "user" ||
    v === "normal"
  ) {
    return 2;
  }
  return 2;
}

function setElResult(el, data) {
  if (!el) return;

  const isEmpty = !data || (typeof data === "string" && data.trim() === "");
  if (isEmpty) {
    clearSuccessStatusTimer();
    hideElResult(el);
    return;
  }

  const message = deriveElMessage(data);
  const isError = isErrorPayload(data, message);

  clearSuccessStatusTimer();
  el.textContent = message;
  el.hidden = false;
  el.classList.toggle("is-error", isError);

  if (!isError && shouldAutoHideSuccess(message)) {
    successTargetEl = el;
    successStatusTimer = window.setTimeout(() => {
      successStatusTimer = null;
      if (successTargetEl === el) {
        successTargetEl = null;
        hideElResult(el);
      }
    }, SUCCESS_STATUS_MS);
  }
}

async function load(options = {}) {
  hideElResult(queryResultEl);
  setExportVisible(false);
  if (userListMetaEl) userListMetaEl.textContent = "正在加载用户列表…";
  if (userListEl) userListEl.innerHTML = "";
  try {
    const res = await fetch(`${apiBase}/api/user/list`, {
      credentials: "include"
    });
    const payload = await res.json();
    const ok = res.ok && payload && typeof payload === "object" && payload.code === 0;
    if (ok) {
      latestUserPayload = payload;
      if (!options.keepPage) userPage = 1;
      renderUserList(payload);
      const n = Array.isArray(payload.data) ? payload.data.length : 0;
      const tip =
        typeof payload.msg === "string" && payload.msg.trim()
          ? payload.msg.trim()
          : `查询成功，共 ${n} 个用户`;
      if (!options.silentSuccessToast) showToast(tip, false);
    } else {
      latestUserPayload = null;
      userPage = 1;
      renderUserList(null);
      const errText =
        (payload && typeof payload === "object" && typeof payload.msg === "string" && payload.msg.trim()) ||
        (!res.ok ? `请求失败（HTTP ${res.status}）` : deriveElMessage(payload) || "查询失败");
      showToast(errText, true);
    }
  } catch (error) {
    showToast(`查询失败：${error.message}`, true);
    latestUserPayload = null;
    userPage = 1;
    renderUserList(null);
  }
}

function getUserRows(payload) {
  if (!payload || !Array.isArray(payload.data)) return [];
  return payload.data.filter((row) => row && typeof row === "object");
}

function formatRoleLabel(row) {
  const roleId = Number(row.roleId ?? row.role_id ?? row.RoleId);
  if (roleId === 1) return "管理员";
  if (roleId === 2) return "普通用户";
  const roleName = String(row.roleName ?? row.role_name ?? row.RoleName ?? "").trim();
  if (!roleName) return "—";
  const normalized = roleName.toLowerCase();
  if (normalized === "admin" || normalized === "administrator") return "管理员";
  if (normalized === "user" || normalized === "normaluser") return "普通用户";
  return roleName;
}

function renderUserList(payload) {
  if (!userListEl) return;
  const rows = getUserRows(payload);
  const keyword = userKeywordEl?.value.trim().toLowerCase() ?? "";
  const filtered = keyword
    ? rows.filter((row) => {
        const loginName = String(row.userName ?? row.user_name ?? row.UserName ?? "").toLowerCase();
        const displayName = String(row.displayName ?? row.display_name ?? row.DisplayName ?? "").toLowerCase();
        return loginName.includes(keyword) || displayName.includes(keyword);
      })
    : rows;

  const pagerApi = window.aura && typeof window.aura.paginateArray === "function" ? window.aura : null;
  const pageData = pagerApi
    ? pagerApi.paginateArray(filtered, userPage, userPageSize)
    : { rows: filtered, page: 1, pageSize: filtered.length || DEFAULT_USER_PAGE_SIZE, total: filtered.length, totalPages: 1 };
  userPage = pageData.page;
  userPageSize = pageData.pageSize;
  const totalCount = rows.length;
  const filteredCount = filtered.length;
  setExportVisible(filteredCount > 0);

  if (userListMetaEl) {
    userListMetaEl.textContent = "";
  }

  if (!payload) {
    userListEl.textContent = "";
    if (userPagerEl) {
      userPagerEl.hidden = true;
      userPagerEl.innerHTML = "";
    }
    return;
  }
  if (!totalCount) {
    userListEl.innerHTML = `<div class="user-list-empty" role="status">暂无可选用户。</div>`;
    if (userPagerEl) {
      userPagerEl.hidden = true;
      userPagerEl.innerHTML = "";
    }
    return;
  }
  if (!filteredCount) {
    userListEl.innerHTML = `<div class="user-list-empty" role="status">未匹配到用户，请调整关键词。</div>`;
    if (userPagerEl) {
      userPagerEl.hidden = true;
      userPagerEl.innerHTML = "";
    }
    return;
  }

  const startIndex = (pageData.page - 1) * pageData.pageSize;
  const bodyRows = pageData.rows
    .map((row, index) => {
      const rawId = row.userId ?? row.user_id ?? row.UserId;
      const userId = Number(rawId);
      const rawUserName = String(row.userName ?? row.user_name ?? row.UserName ?? "");
      const rawDisplayName = String(row.displayName ?? row.display_name ?? row.DisplayName ?? rawUserName);
      const userName = escapeHtml(rawUserName);
      const displayName = escapeHtml(rawDisplayName);
      const roleName = escapeHtml(formatRoleLabel(row));
      const createdCell = formatCreatedAtCell(row.createdAt ?? row.created_at ?? row.CreatedAt);
      const lastLoginCell = formatCreatedAtCell(row.lastLoginAt ?? row.last_login_at ?? row.LastLoginAt);
      const isOn = Number(row.status ?? row.Status ?? -1) === 1;
      const statusLabel = isOn ? "启用" : "禁用";
      const statusClass = isOn ? "user-status is-on" : "user-status is-off";
      const mustChangePassword = row.mustChangePassword === true || row.MustChangePassword === true || row.must_change_password === true;
      const passwordState = mustChangePassword ? '<span class="user-security-tag">需改密</span>' : "";
      const nextStatus = isOn ? 0 : 1;
      const nextStatusLabel = nextStatus === 1 ? "启用" : "禁用";
      if (!userId || !Number.isFinite(userId)) return "";
      const seq = startIndex + index + 1;
      return `<tr data-user-id="${userId}">
        <td class="aura-col-no">${seq}</td>
        <td class="aura-col-id">${userId}</td>
        <td>${userName}</td>
        <td>${displayName}</td>
        <td>${roleName}${passwordState}</td>
        <td class="aura-col-status"><span class="${statusClass}">${statusLabel}</span></td>
        <td class="aura-col-time">${createdCell}</td>
        <td class="aura-col-time">${lastLoginCell}</td>
        <td class="aura-col-action-group">
          <div class="aura-table-actions" role="group" aria-label="用户操作">
            <button
              type="button"
              class="btn-secondary"
              data-user-action="toggle-status"
              data-next-status="${nextStatus}"
              aria-label="切换账号状态为${nextStatusLabel}"
            >${nextStatusLabel}</button>
            <button type="button" class="btn-danger" data-user-action="delete">删除</button>
            <button type="button" class="btn-secondary" data-user-action="reset-password">重置密码</button>
          </div>
        </td>
      </tr>`;
    })
    .filter(Boolean)
    .join("");

  if (!bodyRows) {
    userListEl.innerHTML = `<div class="user-list-empty" role="status">暂无可展示的用户数据。</div>`;
    if (userPagerEl) {
      userPagerEl.hidden = true;
      userPagerEl.innerHTML = "";
    }
    return;
  }

  userListEl.innerHTML = `<div class="aura-data-table-wrap"><table class="aura-data-table" aria-label="用户列表"><thead><tr>
    <th scope="col" class="aura-col-no">序号</th>
    <th scope="col" class="aura-col-id">用户ID</th>
    <th scope="col">用户名</th>
    <th scope="col">昵称</th>
    <th scope="col">角色</th>
    <th scope="col" class="aura-col-status">状态</th>
    <th scope="col" class="aura-col-time">创建时间</th>
    <th scope="col" class="aura-col-time">最后登录时间</th>
    <th scope="col" class="aura-col-action-group">操作</th>
  </tr></thead><tbody>${bodyRows}</tbody></table></div>`;

  if (userPagerEl && window.aura && typeof window.aura.renderPager === "function") {
    window.aura.renderPager(userPagerEl, {
      page: pageData.page,
      pageSize: pageData.pageSize,
      total: filteredCount,
      pageSizeOptions: [15, 30, 45, 60],
      onChange: (nextPage, nextPageSize) => {
        userPage = nextPage;
        userPageSize = nextPageSize;
        renderUserList(latestUserPayload);
      }
    });
  } else if (userPagerEl) {
    userPagerEl.hidden = true;
    userPagerEl.innerHTML = "";
  }
}

async function createUser() {
  setElResult(createResultEl, "");
  const userName = document.getElementById("userName").value.trim();
  const displayName = document.getElementById("displayName").value.trim();
  const password = document.getElementById("password").value.trim();
  const roleId = Number(document.getElementById("roleId").value || 2);
  if (!userName || !password) {
    setElResult(createResultEl, "请输入用户名和密码");
    return;
  }
  try {
    const res = await fetch(`${apiBase}/api/user/create`, {
      method: "POST",
      credentials: "include",
      headers: {
        "Content-Type": "application/json"
      },
      body: JSON.stringify({ userName, displayName, password, roleId })
    });
    const payload = await res.json();
    if (payload && typeof payload === "object" && payload.code === 0) {
      showToast(deriveElMessage(payload) || "创建成功", false);
      hideElResult(createResultEl);
      document.getElementById("userName").value = "";
      document.getElementById("displayName").value = "";
      document.getElementById("password").value = "";
      closeModal(modalCreate);
      void load({ silentSuccessToast: true });
    } else {
      setElResult(createResultEl, payload);
    }
  } catch (error) {
    setElResult(createResultEl, `创建失败：${error.message}`);
  }
}

function downloadUserImportTemplate() {
  const header = ["用户名", "昵称", "密码", "角色"];
  const example = ["示例用户名", "示例昵称", "TempPass#2026", "普通用户"];
  const csvRows = [header.join(","), example.map(toCsvCell).join(",")];
  const blob = new Blob([`\uFEFF${csvRows.join("\r\n")}`], { type: "text/csv;charset=utf-8;" });
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = "用户批量导入模板.csv";
  document.body.appendChild(a);
  a.click();
  a.remove();
  URL.revokeObjectURL(url);
  showToast("已下载导入模板（含表头与一行示例，可删除示例后填写）", false);
}

async function exportUsers() {
  if (window.aura && typeof window.aura.exportDataset === "function") {
    const keyword = String(userKeywordEl?.value ?? "").trim();
    await window.aura.exportDataset({
      apiBase,
      dataset: "user",
      keyword,
      onError: (message) => showToast(message, true)
    });
    return;
  }
  showToast("导出失败：缺少全局导出能力", true);
}

async function importUsersFromCsv(file) {
  if (!(file instanceof File)) return;
  const text = await file.text();
  const lines = text
    .replace(/^\uFEFF/, "")
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter(Boolean);
  if (lines.length < 2) {
    showToast("导入失败：CSV 至少需要一条数据", true);
    return;
  }

  const header = parseCsvLine(lines[0]).map((x) => x.toLowerCase());
  const userNameIndex = Math.max(header.indexOf("用户名"), header.indexOf("username"));
  const displayNameIndex = Math.max(header.indexOf("昵称"), header.indexOf("displayname"));
  const passwordIndex = Math.max(header.indexOf("密码"), header.indexOf("password"));
  const roleNameIndex = pickCsvColumnIndex(header, ["角色", "角色名称", "rolename", "role"]);
  if (userNameIndex < 0 || passwordIndex < 0) {
    showToast("导入失败：表头需包含“用户名”和“密码”", true);
    return;
  }

  let okCount = 0;
  let failCount = 0;
  for (let i = 1; i < lines.length; i += 1) {
    const cols = parseCsvLine(lines[i]);
    const userName = String(cols[userNameIndex] ?? "").trim();
    const password = String(cols[passwordIndex] ?? "").trim();
    const displayName = String(cols[displayNameIndex] ?? userName).trim() || userName;
    const roleCell = roleNameIndex >= 0 ? cols[roleNameIndex] : "";
    const roleId = normalizeImportRoleNameToId(roleCell);
    if (!userName || !password) {
      failCount += 1;
      continue;
    }
    try {
      const res = await fetch(`${apiBase}/api/user/create`, {
        method: "POST",
        credentials: "include",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ userName, displayName, password, roleId })
      });
      const payload = await res.json();
      if (res.ok && payload && payload.code === 0) okCount += 1;
      else failCount += 1;
    } catch {
      failCount += 1;
    }
  }

  showToast(`批量导入完成：成功 ${okCount}，失败 ${failCount}`, failCount > 0 && okCount === 0);
  await load();
}

async function updateUserStatusQuick(userId, nextStatus) {
  if (!Number.isFinite(userId) || (nextStatus !== 0 && nextStatus !== 1)) return;
  try {
    const res = await fetch(`${apiBase}/api/user/status/${userId}`, {
      method: "POST",
      credentials: "include",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ status: nextStatus })
    });
    let payload = null;
    try {
      payload = await res.json();
    } catch {
      payload = { msg: `状态接口异常（HTTP ${res.status}）` };
    }
    if (!res.ok || !payload || typeof payload !== "object" || payload.code !== 0) {
      showToast(payload?.msg || "状态更新失败", true);
      return;
    }
    showToast(payload?.msg || "状态更新成功", false);
    void load({ silentSuccessToast: true });
  } catch (error) {
    showToast(`状态更新失败：${error.message}`, true);
  }
}

function openResetPasswordModal(userId) {
  if (!modalResetPassword || !resetUserIdEl) return;
  resetUserIdEl.value = String(userId ?? "");
  hideElResult(resetPasswordResultEl);
  openModal(modalResetPassword);
}

async function resetUserPassword() {
  setElResult(resetPasswordResultEl, "");
  const userId = Number(resetUserIdEl?.value);

  if (!userId || !Number.isFinite(userId)) {
    setElResult(resetPasswordResultEl, "未选定用户，请从列表重新操作。");
    return;
  }

  try {
    const res = await fetch(`${apiBase}/api/user/${userId}/password`, {
      method: "POST",
      credentials: "include",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({})
    });
    let payload = null;
    try {
      payload = await res.json();
    } catch {
      payload = { msg: `密码接口异常（HTTP ${res.status}）` };
    }
    if (!res.ok || !payload || typeof payload !== "object" || payload.code !== 0) {
      setElResult(resetPasswordResultEl, payload ?? { msg: "密码重置失败" });
      return;
    }

    const temporaryPassword = payload?.data?.temporaryPassword ? `（临时密码：${payload.data.temporaryPassword}）` : "";
    showToast(`${deriveElMessage(payload) || "密码已重置"}${temporaryPassword}`, false, temporaryPassword ? 5200 : 2600);
    hideElResult(resetPasswordResultEl);
    closeModal(modalResetPassword);
    void load();
  } catch (error) {
    setElResult(resetPasswordResultEl, `重置失败：${error.message}`);
  }
}

function openDeleteModal(userId, displayUserId) {
  if (!modalDelete || !deleteUserIdEl) return;
  deleteUserIdEl.value = String(userId ?? "");
  if (deleteUserIdDisplayEl) deleteUserIdDisplayEl.textContent = String(displayUserId ?? userId ?? "—");
  hideElResult(deleteResultEl);
  openModal(modalDelete);
}

async function deleteUser() {
  setElResult(deleteResultEl, "");
  const userId = Number(deleteUserIdEl?.value);
  if (!userId || !Number.isFinite(userId)) {
    setElResult(deleteResultEl, "未选定用户，请从列表重新操作。");
    return;
  }

  try {
    const res = await fetch(`${apiBase}/api/user/${userId}`, {
      method: "DELETE",
      credentials: "include"
    });
    let payload = null;
    try {
      payload = await res.json();
    } catch {
      payload = { msg: `删除接口异常（HTTP ${res.status}）` };
    }
    if (!res.ok || !payload || typeof payload !== "object" || payload.code !== 0) {
      setElResult(deleteResultEl, payload ?? { msg: "删除失败" });
      return;
    }

    showToast(deriveElMessage(payload) || "删除成功", false);
    hideElResult(deleteResultEl);
    closeModal(modalDelete);
    void load();
  } catch (error) {
    setElResult(deleteResultEl, `删除失败：${error.message}`);
  }
}

confirmResetPasswordBtn?.addEventListener("click", resetUserPassword);
confirmDeleteUserBtn?.addEventListener("click", deleteUser);

document.getElementById("load").addEventListener("click", load);
document.getElementById("create").addEventListener("click", createUser);
document.getElementById("exportUsers")?.addEventListener("click", (event) => {
  event.preventDefault();
  event.stopPropagation();
  void exportUsers();
});
document.getElementById("downloadImportTemplate")?.addEventListener("click", () => {
  downloadUserImportTemplate();
});
document.getElementById("openImport")?.addEventListener("click", () => {
  if (!importFileEl) return;
  importFileEl.value = "";
  importFileEl.click();
});
importFileEl?.addEventListener("change", () => {
  const file = importFileEl.files && importFileEl.files[0];
  if (!file) return;
  void importUsersFromCsv(file);
});
userKeywordEl?.addEventListener("input", () => {
  userPage = 1;
  renderUserList(latestUserPayload);
});
userListEl?.addEventListener("click", (event) => {
  const target = event.target;
  if (!(target instanceof HTMLElement)) return;
  const btn = target.closest("button");
  if (!(btn instanceof HTMLElement)) return;

  const tr = btn.closest("tr[data-user-id]");
  if (!(tr instanceof HTMLElement)) return;

  const userIdRaw = tr.dataset.userId ?? "";
  const userId = Number(userIdRaw);
  if (!userId || !Number.isFinite(userId)) return;

  const action = String(btn.dataset.userAction || "").trim();
  if (action === "toggle-status") {
    const nextStatus = Number(btn.dataset.nextStatus ?? "");
    if (nextStatus !== 0 && nextStatus !== 1) return;
    void updateUserStatusQuick(userId, nextStatus);
    return;
  }

  if (action === "delete") {
    openDeleteModal(userId, userId);
    return;
  }

  if (action === "reset-password") {
    openResetPasswordModal(userId);
    return;
  }
});

function refreshBodyScrollLock() {
  const anyOpen = document.querySelector(".aura-modal-root:not([hidden])");
  document.body.style.overflow = anyOpen ? "hidden" : "";
}

function closeModal(root) {
  if (!root) return;
  root.hidden = true;
  refreshBodyScrollLock();
}

function openModal(root) {
  if (!root) return;
  if (modalCreate && modalCreate !== root) closeModal(modalCreate);
  if (modalResetPassword && modalResetPassword !== root) closeModal(modalResetPassword);
  if (modalDelete && modalDelete !== root) closeModal(modalDelete);
  root.hidden = false;
  document.body.style.overflow = "hidden";
  window.requestAnimationFrame(() => {
    root.querySelector(".user-modal-fields input:not([type=hidden]), .user-modal-fields select")?.focus();
  });
}

function bindModalDismiss(root) {
  if (!root) return;
  root.querySelectorAll("[data-aura-modal-dismiss]").forEach((el) => {
    el.addEventListener("click", () => closeModal(root));
  });
}

bindModalDismiss(modalCreate);
bindModalDismiss(modalResetPassword);
bindModalDismiss(modalDelete);

document.getElementById("openCreateModal")?.addEventListener("click", () => {
  hideElResult(createResultEl);
  openModal(modalCreate);
});

document.addEventListener("keydown", (e) => {
  if (e.key !== "Escape") return;
  if (modalCreate && !modalCreate.hidden) closeModal(modalCreate);
  else if (modalResetPassword && !modalResetPassword.hidden) closeModal(modalResetPassword);
  else if (modalDelete && !modalDelete.hidden) closeModal(modalDelete);
});

void load({ silentSuccessToast: true });
