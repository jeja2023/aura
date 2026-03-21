/* 文件：导出页脚本（export.js） | File: Export Script */
const apiBase = "https://localhost:5001";
const resultEl = document.getElementById("result");
const downloadEl = document.getElementById("download");

function getToken() {
  return localStorage.getItem("token") ?? "";
}

function setResult(data) {
  resultEl.textContent = typeof data === "string" ? data : JSON.stringify(data, null, 2);
}

async function run() {
  const type = document.getElementById("type").value;
  const dataset = document.getElementById("dataset").value;
  setResult("加载中...");
  downloadEl.removeAttribute("href");
  downloadEl.textContent = "下载链接";

  try {
    const res = await fetch(`${apiBase}/api/export/${encodeURIComponent(type)}?dataset=${encodeURIComponent(dataset)}`, {
      headers: { Authorization: `Bearer ${getToken()}` }
    });
    const data = await res.json();
    if (data.code === 0 && data.data?.downloadUrl) {
      downloadEl.href = `${apiBase}${data.data.downloadUrl}`;
      downloadEl.textContent = `下载：${data.data.fileName}`;
    }
    setResult(data);
  } catch (error) {
    setResult(`导出失败：${error.message}`);
  }
}

document.getElementById("run").addEventListener("click", run);
