/* 文件：日志页脚本（log.js） | File: Log Script */
const apiBase = "https://localhost:5001";
const resultEl = document.getElementById("result");

function getToken() {
  return localStorage.getItem("token") ?? "";
}

function setResult(data) {
  resultEl.textContent = typeof data === "string" ? data : JSON.stringify(data, null, 2);
}

async function load() {
  const keyword = document.getElementById("keyword").value.trim();
  const page = Number(document.getElementById("page").value || 1);
  const pageSize = Number(document.getElementById("pageSize").value || 20);
  const query = new URLSearchParams({ page: String(page), pageSize: String(pageSize) });
  setResult("加载中...");

  if (keyword) {
    query.set("keyword", keyword);
  }

  try {
    const res = await fetch(`${apiBase}/api/operation/list?${query.toString()}`, {
      headers: { Authorization: `Bearer ${getToken()}` }
    });
    const data = await res.json();
    setResult(data);
  } catch (error) {
    setResult(`查询失败：${error.message}`);
  }
}

document.getElementById("load").addEventListener("click", load);
