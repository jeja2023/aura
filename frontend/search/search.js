/* 文件：搜轨页脚本（search.js） | File: Search Script */
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
    if (Array.isArray(data.data)) return `共 ${data.data.length} 条结果`;
    if (typeof data.msg === "string") return data.msg;
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

function fileToBase64(file) {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => {
      const raw = String(reader.result || "");
      const idx = raw.indexOf(",");
      resolve(idx >= 0 ? raw.slice(idx + 1) : raw);
    };
    reader.onerror = () => reject(reader.error);
    reader.readAsDataURL(file);
  });
}

async function runSearch() {
  const file = document.getElementById("file").files?.[0];
  const topK = Number(document.getElementById("topk").value) || 10;
  if (!file) {
    setResult("请先选择图片");
    return;
  }
  setResult("");
  try {
    const imageBase64 = await fileToBase64(file);
    const extRes = await fetch(`${apiBase}/api/vector/extract`, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        Authorization: `Bearer ${getToken()}`
      },
      body: JSON.stringify({
        imageBase64,
        metadataJson: JSON.stringify({ source: "search-page", fileName: file.name })
      })
    });
    const extData = await extRes.json();
    if (!extRes.ok || extData.code !== 0) {
      setResult(extData?.msg ? extData : { code: 40000, msg: extData?.msg || `提取失败：HTTP ${extRes.status}` });
      return;
    }
    const feature = extData.data.feature;
    const seaRes = await fetch(`${apiBase}/api/vector/search`, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        Authorization: `Bearer ${getToken()}`
      },
      body: JSON.stringify({ feature, topK })
    });
    const seaData = await seaRes.json();
    if (!seaRes.ok || seaData.code !== 0) {
      setResult(seaData);
      return;
    }

    const count = Array.isArray(seaData.data) ? seaData.data.length : 0;
    setResult(`检索完成：共 ${count} 条结果`);
  } catch (error) {
    setResult(`检索失败：${error.message}`);
  }
}

document.getElementById("runBtn").addEventListener("click", runSearch);
