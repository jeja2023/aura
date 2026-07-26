/* 文件：运行配置页脚本（ops-settings.js） */
const resultEl = document.getElementById("result");
const aiBaseUrlsEl = document.getElementById("aiBaseUrls");
const runtimeOverrideStateEl = document.getElementById("runtimeOverrideState");
const updatedMetaEl = document.getElementById("updatedMeta");
const effectiveNodesEl = document.getElementById("effectiveNodes");
const fallbackNodesEl = document.getElementById("fallbackNodes");
const readinessNodesEl = document.getElementById("readinessNodes");
const refreshBtn = document.getElementById("refreshSettings");
const saveBtn = document.getElementById("saveSettings");
const fallbackBtn = document.getElementById("useFallbackSettings");
const readinessBtn = document.getElementById("checkReadiness");

const fallbackEscapeHtml = (value) => String(value ?? "")
  .replaceAll("&", "&amp;")
  .replaceAll("<", "&lt;")
  .replaceAll(">", "&gt;")
  .replaceAll('"', "&quot;")
  .replaceAll("'", "&#39;");
const escapeHtml = window.aura?.escapeHtml || fallbackEscapeHtml;
const pageStatus = window.aura?.createStatusController?.(resultEl) || {
  set(data, options = {}) {
    if (!resultEl) return "";
    const text = typeof data === "string" ? data : (data?.msg || "");
    resultEl.textContent = text;
    resultEl.hidden = !text;
    resultEl.classList.toggle("is-error", Boolean(options.isError || data?.code));
    return text;
  }
};
const requestJson = window.aura?.requestJson || (async (url, options = {}) => {
  const headers = new Headers(options.headers || {});
  const init = { ...options, credentials: options.credentials || "include", headers };
  if (options.body && typeof options.body === "object" && !(options.body instanceof FormData)) {
    if (!headers.has("Content-Type")) headers.set("Content-Type", "application/json");
    init.body = JSON.stringify(options.body);
  }
  const response = await fetch(url, init);
  const text = await response.text();
  let data;
  try {
    data = text ? JSON.parse(text) : null;
  } catch {
    data = { code: response.ok ? 0 : -1, msg: text };
  }
  return { ok: response.ok, status: response.status, data, response };
});

function formatTime(value) {
  if (!value) return "-";
  if (window.aura?.formatDateTime) return window.aura.formatDateTime(value, "-");
  if (typeof window.formatDateTimeDisplay === "function") return window.formatDateTimeDisplay(value, "-");
  return String(value);
}

function normalizeTextAreaValue(value) {
  return String(value || "")
    .split(/[;,\n\r]+/)
    .map((item) => item.trim())
    .filter(Boolean)
    .join("\n");
}

function serializeTextAreaValue() {
  return String(aiBaseUrlsEl?.value || "")
    .split(/[;,\n\r]+/)
    .map((item) => item.trim())
    .filter(Boolean)
    .join(";");
}

function setBusy(busy) {
  if (window.aura?.setBusy) {
    window.aura.setBusy([refreshBtn, saveBtn, fallbackBtn, readinessBtn], busy);
    return;
  }
  [refreshBtn, saveBtn, fallbackBtn, readinessBtn].forEach((btn) => {
    if (btn) btn.disabled = Boolean(busy);
  });
}

function renderNodeList(container, nodes, emptyText) {
  if (!container) return;
  const list = Array.isArray(nodes) ? nodes : [];
  if (!list.length) {
    container.innerHTML = `<li>${escapeHtml(emptyText)}</li>`;
    return;
  }
  container.innerHTML = list.map((node) => `<li>${escapeHtml(node)}</li>`).join("");
}

function renderSettings(payload) {
  const data = payload?.data || {};
  if (aiBaseUrlsEl) aiBaseUrlsEl.value = normalizeTextAreaValue(data.baseUrls || "");
  if (runtimeOverrideStateEl) {
    runtimeOverrideStateEl.textContent = data.hasRuntimeOverride ? "已启用前端运行时配置" : "使用启动默认配置";
  }
  if (updatedMetaEl) {
    const by = data.updatedBy ? `，${data.updatedBy}` : "";
    updatedMetaEl.textContent = data.updatedAt ? `${formatTime(data.updatedAt)}${by}` : "-";
  }
  renderNodeList(effectiveNodesEl, data.effectiveBaseUrls, "暂无生效节点");
  renderNodeList(fallbackNodesEl, data.fallbackBaseUrls, "暂无启动默认节点");
}

function renderHealth(payload) {
  if (!readinessNodesEl) return;
  const nodes = payload?.data?.ai?.nodes;
  if (!Array.isArray(nodes) || nodes.length === 0) {
    readinessNodesEl.innerHTML = '<div class="ops-health-item">暂无节点健康数据</div>';
    return;
  }
  readinessNodesEl.innerHTML = nodes.map((node) => {
    const inferenceReady = node.inferenceReady !== false && node.modelLoaded;
    const ok = Boolean(node.reachable && node.modelLoaded && inferenceReady);
    const reachable = node.reachable ? "可达" : "不可达";
    const model = node.modelLoaded ? "模型已加载" : "模型未就绪";
    const inference = inferenceReady ? "推理可用" : "推理不可用";
    const statusCode = node.statusCode ? `HTTP ${node.statusCode}` : "无 HTTP 状态";
    const detail = node.error || node.message || "";
    return `<div class="ops-health-item">
      <div>
        <strong>${escapeHtml(node.baseUrl || "-")}</strong>
        <div>${escapeHtml(`${reachable} / ${model} / ${inference} / ${statusCode}${detail ? ` / ${detail}` : ""}`)}</div>
      </div>
      <span class="ops-health-status ${ok ? "is-ok" : "is-error"}">${ok ? "正常" : "关注"}</span>
    </div>`;
  }).join("");
}

async function loadSettings(options = {}) {
  setBusy(true);
  try {
    const result = await requestJson("/api/ops/ai-settings");
    if (!result.ok || result.data?.code !== 0) {
      pageStatus.set(result.data?.msg || "查询配置失败", { isError: true });
      return false;
    }
    renderSettings(result.data);
    if (!options.silent) pageStatus.set("配置已刷新");
    return true;
  } catch (error) {
    pageStatus.set(`查询配置失败：${error.message}`, { isError: true });
    return false;
  } finally {
    setBusy(false);
  }
}

async function saveSettings(baseUrls) {
  setBusy(true);
  try {
    const result = await requestJson("/api/ops/ai-settings", { method: "PUT", body: { baseUrls } });
    if (!result.ok || result.data?.code !== 0) {
      pageStatus.set(result.data?.msg || "保存配置失败", { isError: true });
      return false;
    }
    renderSettings(result.data);
    pageStatus.set("AI 推理节点配置已保存");
    await loadReadiness({ silent: true });
    return true;
  } catch (error) {
    pageStatus.set(`保存配置失败：${error.message}`, { isError: true });
    return false;
  } finally {
    setBusy(false);
  }
}

async function loadReadiness(options = {}) {
  setBusy(true);
  try {
    const result = await requestJson("/api/ops/readiness");
    if (!result.ok || result.data?.code !== 0) {
      pageStatus.set(result.data?.msg || "节点检查失败", { isError: true });
      return false;
    }
    renderHealth(result.data);
    if (!options.silent) pageStatus.set("节点状态已刷新");
    return true;
  } catch (error) {
    pageStatus.set(`节点检查失败：${error.message}`, { isError: true });
    return false;
  } finally {
    setBusy(false);
  }
}

refreshBtn?.addEventListener("click", () => { void loadSettings(); });
saveBtn?.addEventListener("click", () => { void saveSettings(serializeTextAreaValue()); });
fallbackBtn?.addEventListener("click", () => {
  if (aiBaseUrlsEl) aiBaseUrlsEl.value = "";
  void saveSettings("");
});
readinessBtn?.addEventListener("click", () => { void loadReadiness(); });
void (async function bootstrap() {
  await loadSettings({ silent: true });
  await loadReadiness({ silent: true });
})();
