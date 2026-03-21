/* 文件：告警页脚本（alert.js） | File: Alert Script */
const apiBase = "https://localhost:5001";
const resultEl = document.getElementById("result");

function getToken() {
  return localStorage.getItem("token") ?? "";
}

function setResult(data) {
  resultEl.textContent = typeof data === "string" ? data : JSON.stringify(data, null, 2);
}

async function createAlert() {
  const alertType = document.getElementById("type").value.trim();
  const detail = document.getElementById("detail").value.trim();
  setResult("创建中...");

  if (!alertType || !detail) {
    setResult("请填写告警类型和详情");
    return;
  }

  try {
    const res = await fetch(`${apiBase}/api/alert/create`, {
      method: "POST",
      headers: { "Content-Type": "application/json", Authorization: `Bearer ${getToken()}` },
      body: JSON.stringify({ alertType, detail })
    });
    const data = await res.json();
    setResult(data);
  } catch (error) {
    setResult(`新增失败：${error.message}`);
  }
}

async function load() {
  setResult("加载中...");

  try {
    const res = await fetch(`${apiBase}/api/alert/list`, {
      headers: { Authorization: `Bearer ${getToken()}` }
    });
    const data = await res.json();
    setResult(data);
  } catch (error) {
    setResult(`查询失败：${error.message}`);
  }
}

document.getElementById("load").addEventListener("click", load);
document.getElementById("create").addEventListener("click", createAlert);
