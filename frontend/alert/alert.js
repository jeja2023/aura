/* 文件：告警页脚本（alert.js） | File: Alert Script */
const apiBase = "";
const resultEl = document.getElementById("result");
const createAlertResultEl = document.getElementById("createAlertResult");
const alertCreateModalEl = document.getElementById("alertCreateModal");
const openCreateAlertModalBtn = document.getElementById("openCreateAlertModal");
const exportAlertBtn = document.getElementById("exportAlert");
let latestAlertRows = [];

function setExportVisible(visible) {
  if (!exportAlertBtn) return;
  if (window.aura && typeof window.aura.setElementVisible === "function") {
    window.aura.setElementVisible(exportAlertBtn, visible);
    return;
  }
  exportAlertBtn.hidden = !visible;
  exportAlertBtn.disabled = !visible;
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

function hideCreateAlertResult() {
  if (!createAlertResultEl) return;
  createAlertResultEl.textContent = "";
  createAlertResultEl.hidden = true;
  createAlertResultEl.classList.remove("is-error");
}

function setCreateAlertResult(data) {
  if (!createAlertResultEl) return;
  const isEmpty = !data || (typeof data === "string" && data.trim() === "");
  if (isEmpty) {
    hideCreateAlertResult();
    return;
  }
  const message = deriveMessage(data);
  const isError = isErrorPayload(data, message);
  createAlertResultEl.textContent = message;
  createAlertResultEl.hidden = false;
  createAlertResultEl.classList.toggle("is-error", isError);
}

function closeAlertCreateModal() {
  if (!alertCreateModalEl) return;
  alertCreateModalEl.hidden = true;
  document.body.style.overflow = "";
}

function openAlertCreateModal() {
  if (!alertCreateModalEl) return;
  alertCreateModalEl.hidden = false;
  document.body.style.overflow = "hidden";
  hideCreateAlertResult();
  const typeEl = document.getElementById("type");
  if (typeEl instanceof HTMLInputElement) typeEl.focus();
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
  setCreateAlertResult("");

  if (!alertType || !detail) {
    setCreateAlertResult("请填写告警类型和详情");
    return;
  }

  try {
    const res = await fetch(`${apiBase}/api/alert/create`, {
      method: "POST",
      credentials: "include",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ alertType, detail })
    });
    const data = await res.json();
    setCreateAlertResult(data);
    if (res.ok && data?.code === 0) {
      const typeEl = document.getElementById("type");
      const detailEl = document.getElementById("detail");
      if (typeEl instanceof HTMLInputElement) typeEl.value = "";
      if (detailEl instanceof HTMLInputElement) detailEl.value = "";
      closeAlertCreateModal();
      void load();
    }
  } catch (error) {
    setCreateAlertResult(`新增失败：${error.message}`);
  }
}

async function load(options = {}) {
  setResult("");
  if (!options.keepExportState) setExportVisible(false);

  try {
    const res = await fetch(`${apiBase}/api/alert/list?limit=500`, {
      credentials: "include"
    });
    const data = await res.json();
    latestAlertRows = Array.isArray(data?.data) ? data.data : [];
    setExportVisible(latestAlertRows.length > 0);
    if (!options.silentSuccessToast || !data || data.code !== 0) {
      setResult(data);
    }
  } catch (error) {
    setResult(`查询失败：${error.message}`);
    latestAlertRows = [];
    setExportVisible(false);
  }
}

openCreateAlertModalBtn?.addEventListener("click", openAlertCreateModal);
alertCreateModalEl?.querySelectorAll("[data-aura-modal-dismiss]").forEach((el) => {
  el.addEventListener("click", () => closeAlertCreateModal());
});

document.getElementById("load").addEventListener("click", load);
document.getElementById("create").addEventListener("click", createAlert);
exportAlertBtn?.addEventListener("click", async (event) => {
  event.preventDefault();
  event.stopPropagation();
  if (window.aura && typeof window.aura.exportDataset === "function") {
    await window.aura.exportDataset({
      apiBase,
      dataset: "alert",
      onError: (message) => setResult(message)
    });
    return;
  }
  setResult("导出失败：缺少全局导出能力");
});
void load({ silentSuccessToast: true });
