/* 文件：告警页脚本（alert.js） | File: Alert Script */
const apiBase = "https://localhost:5001";
const resultEl = document.getElementById("result");

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
  // 输入校验/网络失败通常会直接以字符串形式返回
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

async function createAlert() {
  const alertType = document.getElementById("type").value.trim();
  const detail = document.getElementById("detail").value.trim();
  setResult("");

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
  setResult("");

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
