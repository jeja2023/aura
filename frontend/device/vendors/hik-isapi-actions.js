/* 文件：海康诊断动作模块（hik-isapi-actions.js） | File: Hikvision Diagnostic Actions Module */
(function () {
  let apiBase = "";
  let canRunVendorAction = () => true;
  let initialized = false;

  const hikResultEl = document.getElementById("hikIsapiResult");
  const hikSnapshotWrapEl = document.getElementById("hikSnapshotWrap");
  const hikSnapshotImgEl = document.getElementById("hikSnapshotImg");
  const hikDeviceSelectEl = document.getElementById("hikDeviceSelect");
  const hikResultModalEl = document.getElementById("hikResultModal");
  const hikResultModalTitleEl = document.getElementById("hikResultModalTitle");
  const hikResultModalMetaEl = document.getElementById("hikResultModalMeta");
  const hikResultModalContentEl = document.getElementById("hikResultModalContent");
  const hikResultModalImageWrapEl = document.getElementById("hikResultModalImageWrap");
  const hikResultModalImageEl = document.getElementById("hikResultModalImage");
  let resultModalPrevOverflow = "";

  function setDisplay(text) {
    const msg = String(text || "").trim();
    if (!msg) {
      if (hikResultEl) {
        hikResultEl.textContent = "";
        hikResultEl.hidden = true;
      }
      closeResultModal();
      return;
    }
    if (shouldUseToastOnly(msg)) {
      showToast(msg, isErrorHint(msg));
      return;
    }
    showResultModal(msg, { title: "联调结果" });
  }

  function showToast(message, isError = true, durationMs = 2200) {
    const text = String(message || "").trim();
    if (!text) return;
    if (window.aura && typeof window.aura.toast === "function") {
      window.aura.toast(text, isError, durationMs);
      return;
    }
    if (!hikResultEl) return;
    hikResultEl.textContent = text;
    hikResultEl.hidden = false;
  }

  function shouldUseToastOnly(message) {
    return /请求中|解析中|上传中|转发中|抓图请求中|读取|请先|须在|无效/.test(message);
  }

  function isErrorHint(message) {
    return /请先|失败|错误|异常|无效|拒绝|未授权|无权|禁止/.test(message);
  }

  function showResultModal(content, options = {}) {
    if (!hikResultModalEl || !hikResultModalContentEl) {
      if (hikResultEl) {
        hikResultEl.textContent = String(content || "");
        hikResultEl.hidden = false;
      }
      return;
    }
    if (hikResultModalTitleEl) {
      hikResultModalTitleEl.textContent = String(options.title || "联调结果");
    }
    if (hikResultModalMetaEl) {
      const meta = String(options.meta || "").trim();
      hikResultModalMetaEl.textContent = meta;
      hikResultModalMetaEl.hidden = !meta;
    }
    if (hikResultModalImageWrapEl && hikResultModalImageEl) {
      const imageUrl = String(options.imageUrl || "").trim();
      if (imageUrl) {
        hikResultModalImageEl.src = imageUrl;
        hikResultModalImageWrapEl.hidden = false;
      } else {
        hikResultModalImageEl.removeAttribute("src");
        hikResultModalImageWrapEl.hidden = true;
      }
    }
    hikResultModalContentEl.textContent = String(content || "");
    if (hikResultEl) {
      hikResultEl.textContent = "";
      hikResultEl.hidden = true;
    }
    resultModalPrevOverflow = document.body.style.overflow;
    hikResultModalEl.hidden = false;
    document.body.style.overflow = "hidden";
  }

  function closeResultModal() {
    if (!hikResultModalEl) return;
    hikResultModalEl.hidden = true;
    document.body.style.overflow = resultModalPrevOverflow;
  }

  function hideSnapshot() {
    if (hikSnapshotWrapEl) hikSnapshotWrapEl.hidden = true;
    if (hikSnapshotImgEl) hikSnapshotImgEl.removeAttribute("src");
  }

  function populateDeviceSelect(rows) {
    if (!hikDeviceSelectEl) return;
    const prev = hikDeviceSelectEl.value;
    hikDeviceSelectEl.innerHTML = `<option value="">选择已登记设备…</option>`;
    const list = Array.isArray(rows) ? rows : [];
    for (const row of list) {
      const id = row.deviceId ?? row.DeviceId;
      if (id === undefined || id === null || id === "") continue;
      const name = row.name ?? row.Name ?? "";
      const opt = document.createElement("option");
      opt.value = String(id);
      opt.textContent = `${name ? `${name} · ` : ""}${id}`;
      hikDeviceSelectEl.appendChild(opt);
    }
    if (prev && [...hikDeviceSelectEl.options].some((o) => o.value === prev)) {
      hikDeviceSelectEl.value = prev;
    }
  }

  function setDeviceIdInForm(deviceId) {
    const id = Number(deviceId);
    if (!Number.isFinite(id) || id <= 0) return;
    const manualEl = document.getElementById("hikDeviceIdManual");
    if (manualEl) manualEl.value = String(id);
    if (hikDeviceSelectEl) hikDeviceSelectEl.value = String(id);
  }

  function getDeviceId() {
    if (hikDeviceSelectEl && hikDeviceSelectEl.value) {
      const n = Number(hikDeviceSelectEl.value, 10);
      if (Number.isFinite(n) && n > 0) return n;
    }
    const manualEl = document.getElementById("hikDeviceIdManual");
    if (manualEl && manualEl.value !== "") {
      const n = Number(manualEl.value, 10);
      if (Number.isFinite(n) && n > 0) return n;
    }
    return 0;
  }

  function appendCredentials(payload) {
    const u = document.getElementById("hikUserName")?.value?.trim() ?? "";
    const p = document.getElementById("hikPassword")?.value ?? "";
    if (u) payload.userName = u;
    if (p) payload.password = p;
    return payload;
  }

  function inferImageDataUrl(b64) {
    const t = String(b64 || "").replace(/\s/g, "");
    if (t.startsWith("iVBORw0KGgo")) return `data:image/png;base64,${t}`;
    return `data:image/jpeg;base64,${t}`;
  }

  async function callPost(path, body) {
    const res = await fetch(`${apiBase}${path}`, {
      method: "POST",
      credentials: "include",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body)
    });
    const ct = res.headers.get("content-type") || "";
    let data;
    if (ct.includes("application/json")) {
      data = await res.json();
    } else {
      const t = await res.text();
      data = { code: res.ok ? 0 : res.status, msg: t || res.statusText };
    }
    return { res, data };
  }

  function resultPrefix(res, data) {
    if (!res) return "";
    if (res.status === 401) return "【未授权】请重新登录后再试。\n\n";
    if (res.status === 429 || data?.code === 42901) {
      return `【限流】${typeof data?.msg === "string" ? data.msg : "请求过于频繁，请稍后再试"}\n\n`;
    }
    if (
      res.status === 502 ||
      res.status === 503 ||
      (typeof data?.code === "number" && data.code >= 50200 && data.code < 50400)
    ) {
      const detail =
        typeof data?.detail === "string"
          ? data.detail
          : typeof data?.data?.detail === "string"
            ? data.data.detail
            : "";
      return `【设备或上游异常】${typeof data?.msg === "string" ? data.msg : "调用失败"}${detail ? `（${detail}）` : ""}\n\n`;
    }
    if (!res.ok) {
      return `【HTTP ${res.status}】${typeof data?.msg === "string" ? data.msg : ""}\n\n`;
    }
    if (data && typeof data.code === "number" && data.code !== 0) {
      return `【业务未成功】${typeof data.msg === "string" ? data.msg : `code=${data.code}`}\n\n`;
    }
    return "";
  }

  function setJson(res, data, transform) {
    const prefix = resultPrefix(res, data ?? {});
    let out = data;
    if (typeof transform === "function" && data != null) out = transform(data);
    setDisplay(prefix + JSON.stringify(out ?? {}, null, 2));
  }

  function truncateXmlForPreview(raw, maxChars) {
    if (typeof raw !== "string" || !raw.length) return raw;
    if (raw.length <= maxChars) return raw;
    return `${raw.slice(0, maxChars)}\n\n… 已截断显示（共 ${raw.length} 字符）`;
  }

  function shortenResponseForDisplay(data, maxChars = 8000) {
    if (!data || typeof data !== "object" || !data.data || typeof data.data !== "object") return data;
    const inner = data.data;
    const next = { ...inner };
    let changed = false;
    if (typeof inner.rawXml === "string" && inner.rawXml.length > maxChars) {
      next.rawXml = truncateXmlForPreview(inner.rawXml, maxChars);
      changed = true;
    }
    if (typeof inner.body === "string" && inner.body.length > maxChars) {
      next.body = truncateXmlForPreview(inner.body, maxChars);
      changed = true;
    }
    if (typeof inner.bodyBase64 === "string" && inner.bodyBase64.length > 2400) {
      next.bodyBase64 = `（已省略 Base64，原始长度 ${inner.bodyBase64.length} 字符）`;
      changed = true;
    }
    return changed ? { ...data, data: next } : data;
  }

  function tryBuildGatewayBinaryPreview(data) {
    if (!data || data.code !== 0 || !data.data || !data.data.isBinary || !data.data.bodyBase64) return "";
    const b64 = String(data.data.bodyBase64).replace(/\s/g, "");
    const head = b64.slice(0, 12);
    const ct = String(data.data.contentType || "").toLowerCase();
    const looksImage =
      ct.includes("image") || ct.includes("jpeg") || ct.includes("png") || head.startsWith("/9j") || head.startsWith("iVBORw0KGgo");
    if (!looksImage) return "";
    try {
      return inferImageDataUrl(b64);
    } catch {
      return "";
    }
  }

  function getStreamingChannelId() {
    const el = document.getElementById("hikStreamingChannelId");
    const explicit = el?.value?.trim() ?? "";
    if (explicit) return explicit;
    const channelEl = document.getElementById("hikChannelIndex");
    const streamEl = document.getElementById("hikStreamType");
    const channelIndex = channelEl ? Number(channelEl.value, 10) : 1;
    const streamType = streamEl ? Number(streamEl.value, 10) : 0;
    if (!Number.isFinite(channelIndex) || channelIndex < 1) return "";
    const suffix = streamType === 1 ? "02" : streamType === 2 ? "03" : "01";
    return `${channelIndex}${suffix}`;
  }

  async function callDeviceOpEndpoint(path, loadingHint) {
    if (!canRunVendorAction()) return;
    const deviceId = getDeviceId();
    if (!deviceId) {
      setDisplay("请先选择设备或填写设备 ID。");
      return;
    }
    hideSnapshot();
    setDisplay(loadingHint || "请求中…");
    const { res, data } = await callPost(path, appendCredentials({ deviceId }));
    setJson(res, data, shortenResponseForDisplay);
  }

  function wrapDeviceOp(path, hint) {
    return async () => {
      try {
        await callDeviceOpEndpoint(path, hint);
      } catch (error) {
        setDisplay(`请求失败：${error.message}`);
      }
    };
  }

  function formatGateway403Message(res, data) {
    let hint = `HTTP ${res.status}：`;
    if (data && typeof data === "object") {
      if (data.code === 40301) {
        hint += typeof data.msg === "string" ? data.msg : "网关已在配置中关闭";
      } else if (typeof data.msg === "string" && data.msg) {
        hint += data.msg;
      } else {
        hint += "当前账号可能不是「超级管理员」，或无权访问该接口。请使用超级管理员登录后再试。";
      }
    } else {
      hint += "当前账号可能不是「超级管理员」。请使用超级管理员登录后再试。";
    }
    return hint;
  }

  async function onConnectivity() {
    if (!canRunVendorAction()) return;
    const deviceId = getDeviceId();
    if (!deviceId) {
      setDisplay("请先选择设备或填写设备 ID。");
      return;
    }
    hideSnapshot();
    setDisplay("请求中…");
    try {
      const { res, data } = await callPost("/api/device/hikvision/connectivity", appendCredentials({ deviceId }));
      setJson(res, data);
    } catch (error) {
      setDisplay(`请求失败：${error.message}`);
    }
  }

  async function onDeviceInfo() {
    if (!canRunVendorAction()) return;
    const deviceId = getDeviceId();
    if (!deviceId) {
      setDisplay("请先选择设备或填写设备 ID。");
      return;
    }
    hideSnapshot();
    setDisplay("请求中…");
    try {
      const { res, data } = await callPost("/api/device/hikvision/device-info", appendCredentials({ deviceId }));
      setJson(res, data, shortenResponseForDisplay);
    } catch (error) {
      setDisplay(`请求失败：${error.message}`);
    }
  }

  async function onSnapshot() {
    if (!canRunVendorAction()) return;
    const deviceId = getDeviceId();
    if (!deviceId) {
      setDisplay("请先选择设备或填写设备 ID。");
      return;
    }
    const channelEl = document.getElementById("hikChannelIndex");
    const streamEl = document.getElementById("hikStreamType");
    const channelIndex = channelEl ? Number(channelEl.value, 10) : 1;
    const streamType = streamEl ? Number(streamEl.value, 10) : 0;
    if (!Number.isFinite(channelIndex) || channelIndex < 1 || channelIndex > 512) {
      setDisplay("通道序号须在 1～512 之间。");
      return;
    }
    hideSnapshot();
    setDisplay("抓图请求中…");
    try {
      const { res, data } = await callPost(
        "/api/device/hikvision/snapshot",
        appendCredentials({ deviceId, channelIndex, streamType })
      );
      if (data && data.code === 0 && data.data && data.data.imageBase64) {
        const b64 = data.data.imageBase64;
        const imageUrl = inferImageDataUrl(b64);
        const slim = { ...data, data: { ...data.data, imageBase64: "（已省略，见下方预览图）" } };
        const prefix = resultPrefix(res, slim ?? {});
        showResultModal(prefix + JSON.stringify(slim ?? {}, null, 2), {
          title: "ISAPI 抓图结果",
          imageUrl
        });
      } else {
        setJson(res, data);
      }
    } catch (error) {
      setDisplay(`请求失败：${error.message}`);
    }
  }

  async function onDemoCatalog() {
    if (!canRunVendorAction()) return;
    hideSnapshot();
    setDisplay("请求中…");
    try {
      const res = await fetch(`${apiBase}/api/device/hikvision/demo-catalog`, { credentials: "include" });
      let data;
      try {
        data = await res.json();
      } catch {
        data = { code: res.status, msg: "响应不是合法 JSON" };
      }
      setJson(res, data);
    } catch (error) {
      setDisplay(`请求失败：${error.message}`);
    }
  }

  async function onRequestKeyFrame() {
    if (!canRunVendorAction()) return;
    const deviceId = getDeviceId();
    if (!deviceId) {
      setDisplay("请先选择设备或填写设备 ID。");
      return;
    }
    const streamingChannelId = getStreamingChannelId();
    if (!streamingChannelId) {
      setDisplay("请填写流媒体通道号，或填写有效的通道序号与码流类型以自动生成。");
      return;
    }
    hideSnapshot();
    setDisplay("请求关键帧中…");
    try {
      const { res, data } = await callPost(
        "/api/device/hikvision/streaming/request-key-frame",
        appendCredentials({ deviceId, streamingChannelId })
      );
      setJson(res, data, shortenResponseForDisplay);
    } catch (error) {
      setDisplay(`请求失败：${error.message}`);
    }
  }

  async function onAnalyzeResponse() {
    if (!canRunVendorAction()) return;
    const raw = document.getElementById("hikAnalyzeRaw")?.value ?? "";
    hideSnapshot();
    setDisplay("解析中…");
    try {
      const { res, data } = await callPost("/api/device/hikvision/analyze-response", { raw });
      setJson(res, data);
    } catch (error) {
      setDisplay(`请求失败：${error.message}`);
    }
  }

  function readFileAsDataUrl(file) {
    return new Promise((resolve, reject) => {
      const reader = new FileReader();
      reader.onload = () => resolve(String(reader.result || ""));
      reader.onerror = () => reject(new Error("读取文件失败"));
      reader.readAsDataURL(file);
    });
  }

  async function onSdtUpload() {
    if (!canRunVendorAction()) return;
    const deviceId = getDeviceId();
    if (!deviceId) {
      setDisplay("请先选择设备或填写设备 ID。");
      return;
    }
    const fileInput = document.getElementById("hikSdtFile");
    const file = fileInput?.files?.[0];
    if (!file) {
      setDisplay("请先选择图片文件。");
      return;
    }
    hideSnapshot();
    setDisplay("SDT 上传中…");
    try {
      const dataUrl = await readFileAsDataUrl(file);
      const comma = dataUrl.indexOf(",");
      const imageBase64 = comma >= 0 ? dataUrl.slice(comma + 1) : dataUrl;
      const fileName = file.name && file.name.trim() ? file.name.trim() : "upload.jpg";
      const payload = appendCredentials({
        deviceId,
        fileName,
        imageBase64,
        partContentType: file.type && file.type.trim() ? file.type.trim() : "image/jpeg"
      });
      const { res, data } = await callPost("/api/device/hikvision/sdt/picture-upload", payload);
      setJson(res, data, shortenResponseForDisplay);
    } catch (error) {
      setDisplay(`请求失败：${error.message}`);
    }
  }

  async function onGatewaySend() {
    if (!canRunVendorAction()) return;
    const deviceId = getDeviceId();
    if (!deviceId) {
      showToast("请先选择设备或填写设备 ID。", true);
      return;
    }
    const methodEl = document.getElementById("hikGwMethod");
    const pathEl = document.getElementById("hikGwPath");
    const contentTypeEl = document.getElementById("hikGwContentType");
    const bodyEl = document.getElementById("hikGwBody");
    const bodyB64El = document.getElementById("hikGwBodyB64");
    const preferEl = document.getElementById("hikGwPreferBinary");
    const method = (methodEl?.value || "GET").trim().toUpperCase();
    const pathAndQuery = (pathEl?.value || "").trim();
    if (!pathAndQuery.startsWith("/ISAPI/")) {
      showToast("PathAndQuery 必须以 /ISAPI/ 开头。", true);
      return;
    }
    const bodyB64 = (bodyB64El?.value || "").trim().replace(/\s/g, "");
    const bodyText = bodyB64 ? "" : bodyEl?.value ?? "";
    const contentType = (contentTypeEl?.value || "").trim();
    const preferBinaryResponse = Boolean(preferEl?.checked);
    const payload = appendCredentials({
      deviceId,
      method,
      pathAndQuery,
      preferBinaryResponse
    });
    if (bodyB64) payload.bodyBase64 = bodyB64;
    else if (bodyText) payload.body = bodyText;
    if (contentType) payload.contentType = contentType;

    hideSnapshot();
    setDisplay("网关转发中…");
    try {
      const { res, data } = await callPost("/api/device/hikvision/gateway", payload);
      if (res.status === 403) {
        const head = `${formatGateway403Message(res, data)}\n\n`;
        setDisplay(head + JSON.stringify(data ?? {}, null, 2));
        return;
      }
      if (data && data.code === 0 && data.data && data.data.isBinary && data.data.bodyBase64) {
        const imageUrl = tryBuildGatewayBinaryPreview(data);
        const slim = {
          ...data,
          data: { ...data.data, bodyBase64: "（已省略，若 Content-Type 为图片则见下方预览）" }
        };
        const output = shortenResponseForDisplay(slim);
        const prefix = resultPrefix(res, output ?? {});
        showResultModal(prefix + JSON.stringify(output ?? {}, null, 2), {
          title: "网关转发结果",
          imageUrl
        });
        return;
      }
      setJson(res, data, shortenResponseForDisplay);
    } catch (error) {
      setDisplay(`请求失败：${error.message}`);
    }
  }

  async function onMediaCapabilities() {
    if (!canRunVendorAction()) return;
    hideSnapshot();
    setDisplay("请求中…");
    try {
      const res = await fetch(`${apiBase}/api/media/capabilities`, { credentials: "include" });
      const data = await res.json().catch(() => ({ code: res.status, msg: "响应不是合法 JSON" }));
      setJson(res, data);
    } catch (error) {
      setDisplay(`请求失败：${error.message}`);
    }
  }

  async function onAlertStreamStatus() {
    if (!canRunVendorAction()) return;
    hideSnapshot();
    setDisplay("请求中…");
    try {
      const res = await fetch(`${apiBase}/api/device/hikvision/alert-stream-status`, {
        credentials: "include"
      });
      const data = await res.json().catch(() => ({ code: res.status, msg: "响应不是合法 JSON" }));
      setJson(res, data);
    } catch (error) {
      setDisplay(`请求失败：${error.message}`);
    }
  }

  async function onStreamHint() {
    if (!canRunVendorAction()) return;
    const deviceId = getDeviceId();
    if (!deviceId) {
      setDisplay("请先选择设备或填写设备 ID。");
      return;
    }
    const channelEl = document.getElementById("hikChannelIndex");
    const streamEl = document.getElementById("hikStreamType");
    const channelIndex = channelEl ? Number(channelEl.value) : 1;
    const streamType = streamEl ? Number(streamEl.value) : 0;
    if (!Number.isFinite(channelIndex) || channelIndex < 1 || channelIndex > 512) {
      setDisplay("抓图通道序号无效。");
      return;
    }
    hideSnapshot();
    setDisplay("请求中…");
    try {
      const res = await fetch(`${apiBase}/api/media/hikvision/stream-hint`, {
        method: "POST",
        credentials: "include",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ deviceId, channelIndex, streamType })
      });
      const data = await res.json().catch(() => ({ code: res.status, msg: "响应不是合法 JSON" }));
      setJson(res, data);
    } catch (error) {
      setDisplay(`请求失败：${error.message}`);
    }
  }

  async function startSignalR() {
    if (!window.signalR) return;
    try {
      const connection = new window.signalR.HubConnectionBuilder()
        .withUrl(`${apiBase}/hubs/events`, { withCredentials: true })
        .withAutomaticReconnect()
        .build();
      connection.on("hikvision.alertStream", (payload) => {
        const line = typeof payload === "object" ? JSON.stringify(payload, null, 2) : String(payload);
        const ts = new Date().toLocaleString("zh-CN");
        const head = `[${ts}] SignalR：hikvision.alertStream\n`;
        const prev = hikResultModalContentEl?.textContent || "";
        const next = (head + line + (prev ? `\n\n---\n${prev}` : "")).slice(0, 16000);
        showResultModal(next, { title: "告警长连接事件", meta: "收到 SignalR 推送" });
      });
      await connection.start();
    } catch {
      /* 联调页不阻断：无 SignalR 或 Cookie 未就绪时静默 */
    }
  }

  function bindEvents() {
    document.getElementById("hikConnectivity")?.addEventListener("click", onConnectivity);
    document.getElementById("hikDeviceInfo")?.addEventListener("click", onDeviceInfo);
    document.getElementById("hikSnapshot")?.addEventListener("click", onSnapshot);
    document.getElementById("hikDemoCatalog")?.addEventListener("click", onDemoCatalog);
    document
      .getElementById("hikVideoInputsChannels")
      ?.addEventListener("click", wrapDeviceOp("/api/device/hikvision/video-inputs/channels", "读取模拟通道列表…"));
    document
      .getElementById("hikInputProxyChannels")
      ?.addEventListener("click", wrapDeviceOp("/api/device/hikvision/input-proxy/channels", "读取代理通道…"));
    document.getElementById("hikInputProxyChannelsStatus")?.addEventListener(
      "click",
      wrapDeviceOp("/api/device/hikvision/input-proxy/channels/status", "读取代理通道状态…")
    );
    document.getElementById("hikRequestKeyFrame")?.addEventListener("click", onRequestKeyFrame);
    document
      .getElementById("hikSystemCapabilities")
      ?.addEventListener("click", wrapDeviceOp("/api/device/hikvision/system/capabilities", "读取系统能力…"));
    document
      .getElementById("hikEventCapabilities")
      ?.addEventListener("click", wrapDeviceOp("/api/device/hikvision/event/capabilities", "读取事件能力…"));
    document
      .getElementById("hikZeroVideoChannels")
      ?.addEventListener("click", wrapDeviceOp("/api/device/hikvision/content-mgmt/zero-video-channels", "读取零通道…"));
    document
      .getElementById("hikTrafficCapabilities")
      ?.addEventListener("click", wrapDeviceOp("/api/device/hikvision/traffic/capabilities", "读取交通能力…"));
    document
      .getElementById("hikItcCapability")
      ?.addEventListener("click", wrapDeviceOp("/api/device/hikvision/itc/capability", "读取 ITC 能力…"));
    document.getElementById("hikAnalyzeResponse")?.addEventListener("click", onAnalyzeResponse);
    document.getElementById("hikSdtUpload")?.addEventListener("click", onSdtUpload);
    document.getElementById("hikGatewaySend")?.addEventListener("click", onGatewaySend);
    document.getElementById("hikMediaCapabilities")?.addEventListener("click", onMediaCapabilities);
    document.getElementById("hikStreamHint")?.addEventListener("click", onStreamHint);
    document.getElementById("hikAlertStreamStatus")?.addEventListener("click", onAlertStreamStatus);
    hikResultModalEl?.querySelectorAll("[data-hik-result-dismiss]").forEach((el) => {
      el.addEventListener("click", () => closeResultModal());
    });
  }

  function init(options = {}) {
    apiBase = String(options.apiBase || "");
    canRunVendorAction =
      typeof options.canRunVendorAction === "function" ? options.canRunVendorAction : () => true;
    if (initialized) return;
    bindEvents();
    initialized = true;
  }

  window.auraHikIsapiActions = {
    init,
    populateDeviceSelect,
    setDeviceIdInForm,
    setDisplay,
    startSignalR
  };
})();
