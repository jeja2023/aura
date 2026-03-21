/* 文件：抓拍页脚本（capture.js） | File: Capture Script */
const apiBase = "https://localhost:5001";
const resultEl = document.getElementById("result");

function getToken() {
  return localStorage.getItem("token") ?? "";
}

function setResult(data) {
  resultEl.textContent = typeof data === "string" ? data : JSON.stringify(data, null, 2);
}

async function createMock() {
  const deviceId = Number(document.getElementById("deviceId").value || 1);
  const channelNo = Number(document.getElementById("channelNo").value || 1);
  const metadataJson = document.getElementById("meta").value || "";
  setResult("创建中...");

  try {
    const res = await fetch(`${apiBase}/api/capture/mock`, {
      method: "POST",
      headers: { "Content-Type": "application/json", Authorization: `Bearer ${getToken()}` },
      body: JSON.stringify({ deviceId, channelNo, metadataJson })
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
    const res = await fetch(`${apiBase}/api/capture/list`, {
      headers: { Authorization: `Bearer ${getToken()}` }
    });
    const data = await res.json();
    setResult(data);
  } catch (error) {
    setResult(`查询失败：${error.message}`);
  }
}

document.getElementById("load").addEventListener("click", load);
document.getElementById("create").addEventListener("click", createMock);
