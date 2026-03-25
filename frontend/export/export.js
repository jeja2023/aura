/* 文件：导出页脚本（export.js） | File: Export Script */
const apiBase = "";
const statusEl = document.getElementById("exportStatus");
const downloadEl = document.getElementById("download");

let successStatusTimer = null;
const SUCCESS_STATUS_MS = 5000;

function clearSuccessStatusTimer() {
  if (successStatusTimer != null) {
    clearTimeout(successStatusTimer);
    successStatusTimer = null;
  }
}

function setStatus(message, isError) {
  if (!statusEl) return;
  if (!message) {
    statusEl.textContent = "";
    statusEl.hidden = true;
    statusEl.classList.remove("is-error");
    return;
  }
  statusEl.textContent = message;
  statusEl.hidden = false;
  statusEl.classList.toggle("is-error", Boolean(isError));
}

async function run() {
  clearSuccessStatusTimer();
  const type = document.getElementById("type").value;
  const dataset = document.getElementById("dataset").value;
  setStatus("");
  downloadEl.removeAttribute("href");
  downloadEl.textContent = "下载链接";

  try {
    const res = await fetch(`${apiBase}/api/export/${encodeURIComponent(type)}?dataset=${encodeURIComponent(dataset)}`, {
      credentials: "include"
    });
    const data = await res.json();

    if (!res.ok) {
      setStatus(data?.msg || `请求失败：HTTP ${res.status}`, true);
      return;
    }
    if (data.code !== 0) {
      setStatus(data.msg || "导出失败", true);
      return;
    }

    if (data.data?.downloadUrl) {
      downloadEl.href = `${apiBase}${data.data.downloadUrl}`;
      downloadEl.textContent = `下载：${data.data.fileName || "导出文件"}`;
    }

    setStatus("导出文件已生成，请使用上方链接下载。", false);
    clearSuccessStatusTimer();
    successStatusTimer = window.setTimeout(() => {
      successStatusTimer = null;
      setStatus("");
    }, SUCCESS_STATUS_MS);
  } catch (error) {
    setStatus(`导出失败：${error.message}`, true);
  }
}

document.getElementById("run").addEventListener("click", run);
