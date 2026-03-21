/* 文件：用户页脚本（user.js） | File: User Script */
const apiBase = "https://localhost:5001";
const resultEl = document.getElementById("result");

function getToken() {
  return localStorage.getItem("token") ?? "";
}

function setResult(data) {
  resultEl.textContent = typeof data === "string" ? data : JSON.stringify(data, null, 2);
}

async function load() {
  setResult("加载中...");
  try {
    const res = await fetch(`${apiBase}/api/user/list`, {
      headers: { Authorization: `Bearer ${getToken()}` }
    });
    setResult(await res.json());
  } catch (error) {
    setResult(`查询失败：${error.message}`);
  }
}

async function createUser() {
  setResult("创建中...");
  const userName = document.getElementById("userName").value.trim();
  const password = document.getElementById("password").value.trim();
  const roleId = Number(document.getElementById("roleId").value || 2);
  if (!userName || !password) {
    setResult("请输入用户名和密码");
    return;
  }
  try {
    const res = await fetch(`${apiBase}/api/user/create`, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        Authorization: `Bearer ${getToken()}`
      },
      body: JSON.stringify({ userName, password, roleId })
    });
    setResult(await res.json());
  } catch (error) {
    setResult(`创建失败：${error.message}`);
  }
}

async function updateStatus() {
  setResult("更新中...");
  const userId = Number(document.getElementById("statusUserId").value);
  const status = Number(document.getElementById("statusValue").value);
  if (!userId || (status !== 0 && status !== 1)) {
    setResult("请输入有效的用户ID和状态(0或1)");
    return;
  }
  try {
    const res = await fetch(`${apiBase}/api/user/status/${userId}`, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        Authorization: `Bearer ${getToken()}`
      },
      body: JSON.stringify({ status })
    });
    setResult(await res.json());
  } catch (error) {
    setResult(`更新失败：${error.message}`);
  }
}

document.getElementById("load").addEventListener("click", load);
document.getElementById("create").addEventListener("click", createUser);
document.getElementById("updateStatus").addEventListener("click", updateStatus);
