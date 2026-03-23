/* 文件：角色页脚本（role.js） | File: Role Script */
const apiBase = "https://localhost:5001";
const resultEl = document.getElementById("result");
const permissionDropdownEl = document.getElementById("permissionDropdown");
const permissionToggleEl = document.getElementById("permissionToggle");
const permissionMenuEl = document.getElementById("permissionMenu");

/** 成功提示自动消失定时器 */
let successStatusTimer = null;
const SUCCESS_STATUS_MS = 5000;

function getToken() {
  return localStorage.getItem("token") ?? "";
}

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
    return /失败|错误|异常|请/.test(message);
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

async function load() {
  setResult("");
  try {
    const res = await fetch(`${apiBase}/api/role/list`, {
      headers: { Authorization: `Bearer ${getToken()}` }
    });
    setResult(await res.json());
  } catch (error) {
    setResult(`查询失败：${error.message}`);
  }
}

async function createRole() {
  setResult("");
  const roleName = document.getElementById("roleName").value.trim();
  const selectedPermissions = Array.from(permissionMenuEl?.querySelectorAll('input[type="checkbox"]:checked') ?? []).map((el) => el.value);
  const permissionJson = JSON.stringify(selectedPermissions);
  if (!roleName) {
    setResult("请输入角色名");
    return;
  }
  try {
    const res = await fetch(`${apiBase}/api/role/create`, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        Authorization: `Bearer ${getToken()}`
      },
      body: JSON.stringify({ roleName, permissionJson })
    });
    setResult(await res.json());
  } catch (error) {
    setResult(`创建失败：${error.message}`);
  }
}

function setPermissionToggleText() {
  if (!permissionToggleEl || !permissionMenuEl) return;
  const checked = Array.from(permissionMenuEl.querySelectorAll('input[type="checkbox"]:checked'));
  permissionToggleEl.textContent = checked.length > 0 ? `已选择 ${checked.length} 项权限` : "权限列表（可多选）";
}

function togglePermissionMenu(show) {
  if (!permissionMenuEl || !permissionToggleEl) return;
  const next = show ?? permissionMenuEl.hidden;
  permissionMenuEl.hidden = !next;
  permissionToggleEl.setAttribute("aria-expanded", String(next));
}

if (permissionToggleEl) {
  permissionToggleEl.addEventListener("click", () => {
    togglePermissionMenu();
  });
}

if (permissionMenuEl) {
  permissionMenuEl.addEventListener("change", setPermissionToggleText);
}

document.addEventListener("click", (e) => {
  if (!permissionDropdownEl) return;
  if (permissionDropdownEl.contains(e.target)) return;
  togglePermissionMenu(false);
});

setPermissionToggleText();

document.getElementById("load").addEventListener("click", load);
document.getElementById("create").addEventListener("click", createRole);
