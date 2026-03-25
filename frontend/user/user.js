/* 文件：用户页脚本（user.js） | File: User Script */
const apiBase = "";
const createResultEl = document.getElementById("createResult");
const queryResultEl = document.getElementById("queryResult");
const updateResultEl = document.getElementById("updateResult");
const userListEl = document.getElementById("userList");
const userListMetaEl = document.getElementById("userListMeta");
const userKeywordEl = document.getElementById("userKeyword");
const statusUserIdEl = document.getElementById("statusUserId");
let latestUserPayload = null;
const USER_LIST_LIMIT = 200;

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
    return /失败|错误|异常|请/.test(message);
  }
  return false;
}

function shouldAutoHideSuccess(message) {
  // 选择了用户ID后用户可能还要继续更新，不建议自动消失
  if (typeof message === "string" && message.startsWith("已选择用户ID")) return false;
  return true;
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

async function load() {
  setElResult(queryResultEl, "");
  try {
    const res = await fetch(`${apiBase}/api/user/list`, {
      credentials: "include"
    });
    const payload = await res.json();
    latestUserPayload = payload;
    renderUserList(payload);
    setElResult(queryResultEl, payload);
  } catch (error) {
    setElResult(queryResultEl, `查询失败：${error.message}`);
    latestUserPayload = null;
    renderUserList(null);
  }
}

function getUserRows(payload) {
  if (!payload || !Array.isArray(payload.data)) return [];
  return payload.data.filter((row) => row && typeof row === "object");
}

function renderUserList(payload) {
  if (!userListEl) return;
  const rows = getUserRows(payload);
  const keyword = userKeywordEl?.value.trim().toLowerCase() ?? "";
  const filtered = keyword
    ? rows.filter((row) => String(row.userName ?? "").toLowerCase().includes(keyword))
    : rows;

  const totalCount = rows.length;
  const filteredCount = filtered.length;
  const shownCount = Math.min(USER_LIST_LIMIT, filteredCount);

  if (userListMetaEl) {
    if (!payload) {
      userListMetaEl.textContent = "请先点击“查询用户”。";
    } else if (!totalCount) {
      userListMetaEl.textContent = "共 0 个用户。";
    } else if (!filteredCount) {
      userListMetaEl.textContent = `共 ${totalCount} 个用户，未匹配到 ${keyword ? `“${keyword}”` : "关键词"}。`;
    } else {
      userListMetaEl.textContent = `共 ${totalCount} 个用户，匹配 ${filteredCount} 个，展示前 ${shownCount} 个（最多 ${USER_LIST_LIMIT}）。`;
    }
  }

  if (!totalCount) {
    userListEl.textContent = "暂无可选用户，请先查询用户。";
    return;
  }
  if (!filteredCount) {
    userListEl.textContent = "未匹配到用户，请调整关键词。";
    return;
  }

  const listHtml = filtered
    .slice(0, USER_LIST_LIMIT)
    .map((row) => {
      const userId = Number(row.userId ?? 0);
      const userName = escapeHtml(row.userName ?? "");
      const status = Number(row.status ?? -1) === 1 ? "启用" : "禁用";
      if (!userId) return "";
      return `<button type="button" class="user-list-item" data-user-id="${userId}">${userName}（ID:${userId}，${status}）</button>`;
    })
    .join("");

  userListEl.innerHTML = listHtml || "暂无可选用户，请先查询用户。";
}

async function createUser() {
  setElResult(createResultEl, "");
  const userName = document.getElementById("userName").value.trim();
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
      body: JSON.stringify({ userName, password, roleId })
    });
    setElResult(createResultEl, await res.json());
  } catch (error) {
    setElResult(createResultEl, `创建失败：${error.message}`);
  }
}

async function updateStatus() {
  setElResult(updateResultEl, "");
  const userId = Number(statusUserIdEl.value);
  const status = Number(document.getElementById("statusValue").value);
  if (!userId || (status !== 0 && status !== 1)) {
    setElResult(updateResultEl, "请输入有效的用户ID和状态(0或1)");
    return;
  }
  try {
    const res = await fetch(`${apiBase}/api/user/status/${userId}`, {
      method: "POST",
      credentials: "include",
      headers: {
        "Content-Type": "application/json"
      },
      body: JSON.stringify({ status })
    });
    setElResult(updateResultEl, await res.json());
  } catch (error) {
    setElResult(updateResultEl, `更新失败：${error.message}`);
  }
}

document.getElementById("load").addEventListener("click", load);
document.getElementById("create").addEventListener("click", createUser);
document.getElementById("updateStatus").addEventListener("click", updateStatus);
userKeywordEl?.addEventListener("input", () => {
  renderUserList(latestUserPayload);
});
userListEl?.addEventListener("click", (event) => {
  const target = event.target;
  if (!(target instanceof HTMLElement)) return;
  const button = target.closest(".user-list-item");
  if (!(button instanceof HTMLElement)) return;
  const userId = button.dataset.userId ?? "";
  if (!userId) return;
  statusUserIdEl.value = userId;
  setElResult(updateResultEl, `已选择用户ID：${userId}`);
});

function setupTabs() {
  const tabCreate = document.getElementById("tab-create");
  const tabQuery = document.getElementById("tab-query");
  const tabUpdate = document.getElementById("tab-update");
  const panelCreate = document.getElementById("panel-create");
  const panelQuery = document.getElementById("panel-query");
  const panelUpdate = document.getElementById("panel-update");
  if (!(tabCreate && tabQuery && tabUpdate && panelCreate && panelQuery && panelUpdate)) return;

  const tabs = [
    { tabEl: tabCreate, panelEl: panelCreate, selected: true },
    { tabEl: tabQuery, panelEl: panelQuery, selected: false },
    { tabEl: tabUpdate, panelEl: panelUpdate, selected: false }
  ];

  function setActive(targetTabEl, targetPanelEl) {
    tabs.forEach((t) => {
      const isActive = t.tabEl === targetTabEl;
      t.tabEl.classList.toggle("is-active", isActive);
      t.tabEl.setAttribute("aria-selected", isActive ? "true" : "false");
      t.panelEl.classList.toggle("is-active", isActive);
    });
  }

  tabCreate.addEventListener("click", () => setActive(tabCreate, panelCreate));
  tabQuery.addEventListener("click", () => setActive(tabQuery, panelQuery));
  tabUpdate.addEventListener("click", () => setActive(tabUpdate, panelUpdate));
}

setupTabs();
