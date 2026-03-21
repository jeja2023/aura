/* 文件：角色页脚本（role.js） | File: Role Script */
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
    const res = await fetch(`${apiBase}/api/role/list`, {
      headers: { Authorization: `Bearer ${getToken()}` }
    });
    setResult(await res.json());
  } catch (error) {
    setResult(`查询失败：${error.message}`);
  }
}

async function createRole() {
  setResult("创建中...");
  const roleName = document.getElementById("roleName").value.trim();
  const permissionJson = document.getElementById("permissionJson").value.trim() || "[]";
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

document.getElementById("load").addEventListener("click", load);
document.getElementById("create").addEventListener("click", createRole);
