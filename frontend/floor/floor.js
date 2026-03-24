/* 文件：楼层页脚本（floor.js） | File: Floor Script */
const apiBase = "https://localhost:5001";
const resultEl = document.getElementById("result");
const previewEl = document.getElementById("preview");

/** 成功提示自动消失定时器 */
let successStatusTimer = null;
const SUCCESS_STATUS_MS = 5000;

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

async function uploadAndCreate() {
  const fileEl = document.getElementById("file");
  const file = fileEl.files?.[0];
  const nodeId = Number(document.getElementById("nodeId").value);
  const scaleRatio = Number(document.getElementById("scaleRatio").value);
  if (!file) {
    setResult("请先选择文件");
    return;
  }

  setResult("");
  try {
    const form = new FormData();
    form.append("file", file);
    const uploadRes = await fetch(`${apiBase}/api/floor/upload`, {
      method: "POST",
      credentials: "include",
      body: form
    });
    const uploadData = await uploadRes.json();
    if (uploadData.code !== 0) {
      setResult(uploadData);
      return;
    }

    const filePath = uploadData.data.filePath;
    previewEl.src = `${apiBase}${filePath}`;
    const createRes = await fetch(`${apiBase}/api/floor/create`, {
      method: "POST",
      credentials: "include",
      headers: {
        "Content-Type": "application/json"
      },
      body: JSON.stringify({ nodeId, filePath, scaleRatio })
    });
    const createData = await createRes.json();
    setResult({ upload: uploadData, create: createData });
  } catch (error) {
    setResult(`上传失败：${error.message}`);
  }
}

async function loadList() {
  setResult("");
  try {
    const res = await fetch(`${apiBase}/api/floor/list`, {
      credentials: "include"
    });
    const data = await res.json();
    setResult(data);
  } catch (error) {
    setResult(`查询失败：${error.message}`);
  }
}

document.getElementById("uploadBtn").addEventListener("click", uploadAndCreate);
document.getElementById("listBtn").addEventListener("click", loadList);
