/* 文件：运行配置页脚本（ops-settings.js） | File: Runtime Settings Script */
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

let successStatusTimer = null;

function escapeHtml(value) {
  return String(value ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll("\"", "&quot;")
    .replaceAll("'", "&#39;");
}

function formatTime(value) {
  if (!value) return "-";
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
  [refreshBtn, saveBtn, fallbackBtn, readinessBtn].forEach((btn) => {
    if (btn) btn.disabled = Boolean(busy);
  });
}

function setResult(message, isError = false) {
  if (!resultEl) return;
  const text = String(message || "").trim();
  if (!text) {
    resultEl.textContent = "";
    resultEl.hidden = true;
    resultEl.classList.remove("is-error");
    return;
  }

  if (successStatusTimer != null) {
    clearTimeout(successStatusTimer);
    successStatusTimer = null;
  }

  resultEl.textContent = text;
  resultEl.hidden = false;
  resultEl.classList.toggle("is-error", Boolean(isError));
  if (!isError) {
    successStatusTimer = window.setTimeout(() => {
      successStatusTimer = null;
      setResult("");
    }, 5000);
  }
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
  if (aiBaseUrlsEl) {
    aiBaseUrlsEl.value = normalizeTextAreaValue(data.baseUrls || "");
  }
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
    const ok = Boolean(node.reachable && node.modelLoaded);
    const reachable = node.reachable ? "可达" : "不可达";
    const model = node.modelLoaded ? "模型已加载" : "模型未就绪";
    const statusCode = node.statusCode ? `HTTP ${node.statusCode}` : "无 HTTP 状态";
    const detail = node.error || node.message || "";
    return `<div class="ops-health-item">
      <div>
        <strong>${escapeHtml(node.baseUrl || "-")}</strong>
        <div>${escapeHtml(`${reachable} / ${model} / ${statusCode}${detail ? ` / ${detail}` : ""}`)}</div>
      </div>
      <span class="ops-health-status ${ok ? "is-ok" : "is-error"}">${ok ? "正常" : "关注"}</span>
    </div>`;
  }).join("");
}

async function requestJson(url, options = {}) {
  if (window.aura && typeof window.aura.requestJson === "function") {
    return await window.aura.requestJson(url, options);
  }

  const res = await fetch(url, {
    ...options,
    credentials: options.credentials || "include",
    headers: {
      "Content-Type": "application/json",
      ...(options.headers || {})
    },
    body: options.body && typeof options.body === "object" ? JSON.stringify(options.body) : options.body
  });
  const data = await res.json();
  return { ok: res.ok, status: res.status, data };
}

async function loadSettings(options = {}) {
  setBusy(true);
  try {
    const result = await requestJson("/api/ops/ai-settings");
    if (!result.ok || result.data?.code !== 0) {
      setResult(result.data?.msg || "查询配置失败", true);
      return false;
    }
    renderSettings(result.data);
    if (!options.silent) setResult("配置已刷新");
    return true;
  } catch (error) {
    setResult(`查询配置失败：${error.message}`, true);
    return false;
  } finally {
    setBusy(false);
  }
}

async function saveSettings(baseUrls) {
  setBusy(true);
  try {
    const result = await requestJson("/api/ops/ai-settings", {
      method: "PUT",
      body: { baseUrls }
    });
    if (!result.ok || result.data?.code !== 0) {
      setResult(result.data?.msg || "保存配置失败", true);
      return false;
    }
    renderSettings(result.data);
    setResult("AI 推理节点配置已保存");
    await loadReadiness({ silent: true });
    return true;
  } catch (error) {
    setResult(`保存配置失败：${error.message}`, true);
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
      setResult(result.data?.msg || "节点检查失败", true);
      return false;
    }
    renderHealth(result.data);
    if (!options.silent) setResult("节点状态已刷新");
    return true;
  } catch (error) {
    setResult(`节点检查失败：${error.message}`, true);
    return false;
  } finally {
    setBusy(false);
  }
}

refreshBtn?.addEventListener("click", () => {
  void loadSettings();
});

saveBtn?.addEventListener("click", () => {
  void saveSettings(serializeTextAreaValue());
});

fallbackBtn?.addEventListener("click", () => {
  if (aiBaseUrlsEl) aiBaseUrlsEl.value = "";
  void saveSettings("");
});

readinessBtn?.addEventListener("click", () => {
  void loadReadiness();
});

void (async function bootstrap() {
  await loadSettings({ silent: true });
  await loadReadiness({ silent: true });
})();
