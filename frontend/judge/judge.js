/* 文件：研判页脚本（judge.js） | File: Judge Script */
const apiBase = "";
const resultEl = document.getElementById("result");
const exportJudgeBtn = document.getElementById("exportJudge");
let latestJudgeRows = [];

function setExportVisible(visible) {
  if (!exportJudgeBtn) return;
  if (window.aura && typeof window.aura.setElementVisible === "function") {
    window.aura.setElementVisible(exportJudgeBtn, visible);
    return;
  }
  exportJudgeBtn.hidden = !visible;
  exportJudgeBtn.disabled = !visible;
}

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

function getDate() {
  const val = document.getElementById("date").value;
  if (val) return val;
  const now = new Date();
  const y = now.getFullYear();
  const m = String(now.getMonth() + 1).padStart(2, "0");
  const d = String(now.getDate()).padStart(2, "0");
  return `${y}-${m}-${d}`;
}

async function post(path, body) {
  const res = await fetch(`${apiBase}${path}`, {
    method: "POST",
    credentials: "include",
    headers: {
      "Content-Type": "application/json"
    },
    body: JSON.stringify(body)
  });
  return await res.json();
}

async function load(options = {}) {
  setResult("");
  if (!options.keepExportState) setExportVisible(false);

  try {
    const date = getDate();
    const res = await fetch(`${apiBase}/api/judge/daily?date=${encodeURIComponent(date)}&limit=2000`, {
      credentials: "include"
    });
    const data = await res.json();
    latestJudgeRows = Array.isArray(data?.data) ? data.data : [];
    setExportVisible(latestJudgeRows.length > 0);
    if (!options.silentSuccessToast || !data || data.code !== 0) {
      setResult(data);
    }
  } catch (error) {
    setResult(`查询失败：${error.message}`);
    latestJudgeRows = [];
    setExportVisible(false);
  }
}

async function runDaily() {
  setResult("");
  try {
    const date = getDate();
    const cutoffHour = Number(document.getElementById("cutoffHour").value);
    const data = await post("/api/judge/run/daily", { date, cutoffHour });
    setResult(data);
  } catch (error) {
    setResult(`执行失败：${error.message}`);
  }
}

async function runHome() {
  setResult("");
  try {
    const data = await post("/api/judge/run/home", { date: getDate() });
    setResult(data);
  } catch (error) {
    setResult(`执行失败：${error.message}`);
  }
}

async function runAbnormal() {
  setResult("");
  try {
    const data = await post("/api/judge/run/abnormal", {
      date: getDate(),
      groupThreshold: 2,
      stayMinutes: 120
    });
    setResult(data);
  } catch (error) {
    setResult(`执行失败：${error.message}`);
  }
}

async function runNight() {
  setResult("");
  try {
    const cutoffHour = Number(document.getElementById("cutoffHour").value);
    const data = await post("/api/judge/run/night", { date: getDate(), cutoffHour });
    setResult(data);
  } catch (error) {
    setResult(`执行失败：${error.message}`);
  }
}

document.getElementById("load").addEventListener("click", load);
document.getElementById("runDaily").addEventListener("click", runDaily);
document.getElementById("runHome").addEventListener("click", runHome);
document.getElementById("runAbnormal").addEventListener("click", runAbnormal);
document.getElementById("runNight").addEventListener("click", runNight);
exportJudgeBtn?.addEventListener("click", async (event) => {
  event.preventDefault();
  event.stopPropagation();
  if (window.aura && typeof window.aura.exportDataset === "function") {
    await window.aura.exportDataset({
      apiBase,
      dataset: "judge",
      onError: (message) => setResult(message)
    });
    return;
  }
  setResult("导出失败：缺少全局导出能力");
});
void load({ silentSuccessToast: true });
