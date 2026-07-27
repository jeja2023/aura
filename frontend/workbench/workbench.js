const $ = (selector) => document.querySelector(selector);
const $$ = (selector) => Array.from(document.querySelectorAll(selector));
const requestJson = (url, options = {}) => window.aura.requestJson(url, options);
const escapeHtml = (value) => window.aura.escapeHtml(value ?? "");
const formatTime = (value) => window.aura.formatDateTime(value, "-");
const app = {
  session: null,
  tenants: [],
  tenantId: null,
  activeTab: "events",
  eventPage: 1,
  eventPageSize: 20,
  casePage: 1,
  casePageSize: 20,
  selectedEvent: null,
  selectedCase: null,
  investigationId: null,
  highRisk: null,
  myTasks: false,
  controlledPlan: null
};

function uuid() {
  if (window.crypto?.randomUUID) return window.crypto.randomUUID();
  return `${Date.now().toString(16)}-4000-8000-${Math.random().toString(16).slice(2).padEnd(12, "0").slice(0, 12)}`;
}

async function api(url, options = {}) {
  const result = await requestJson(url, options);
  if (!result.ok || result.data?.code !== 0) {
    const error = new Error(result.data?.msg || `HTTP ${result.status}`);
    error.status = result.status;
    error.payload = result.data;
    throw error;
  }
  return result.data;
}

async function apiForm(url, formData) {
  const response = await fetch(url, { method: "POST", body: formData, credentials: "include" });
  let data = null;
  try { data = await response.json(); } catch { data = { msg: response.statusText }; }
  if (!response.ok || data?.code !== 0) {
    const error = new Error(data?.msg || `HTTP ${response.status}`);
    error.status = response.status;
    error.payload = data;
    throw error;
  }
  return data;
}

function toast(message, isError = false) {
  window.aura.toast(message, isError, isError ? 3800 : 2200);
}

function can(permission) {
  if (app.session?.role === "super_admin") return true;
  const permissions = new Set(Array.isArray(app.session?.permissions) ? app.session.permissions : []);
  return permissions.has("all") || permissions.has(permission);
}

function applyPermissions() {
  $$('[data-permission]').forEach((element) => { element.hidden = !can(element.dataset.permission); });
}

function requiredTenant() {
  if (!app.tenantId) throw new Error("请选择租户");
  return app.tenantId;
}

function stateBadge(value) {
  const text = String(value || "unknown");
  return `<span class="wb-state" data-state="${escapeHtml(text.toLowerCase())}">${escapeHtml(text)}</span>`;
}

function severityBadge(value) {
  const labels = { critical: "紧急", high: "高", medium: "中", low: "低" };
  const level = String(value || "medium").toLowerCase();
  return `<span class="wb-severity" data-level="${escapeHtml(level)}">${labels[level] || escapeHtml(level)}</span>`;
}

function emptyRow(columns, message = "暂无数据") {
  return `<tr><td colspan="${columns}">${escapeHtml(message)}</td></tr>`;
}

function parseJson(value, fallback = {}) {
  if (value && typeof value === "object") return value;
  try { return JSON.parse(value || "{}"); } catch { return fallback; }
}

function formObject(form) {
  return Object.fromEntries(new FormData(form).entries());
}

function localDrafts() {
  try {
    const value = JSON.parse(window.localStorage.getItem("aura.workbench.drafts") || "[]");
    return Array.isArray(value) ? value : [];
  } catch { return []; }
}

function saveLocalDrafts(rows) {
  window.localStorage.setItem("aura.workbench.drafts", JSON.stringify(rows));
  updateDraftCount();
}

function updateDraftCount() {
  $("#draftCount").textContent = `草稿 ${localDrafts().length}`;
}

function updateConnectionState() {
  const online = navigator.onLine;
  const element = $("#connectionState");
  element.dataset.state = online ? "online" : "offline";
  element.textContent = online ? "在线" : "离线";
}

async function recordAnalytics(eventName, objectType = null, objectId = null, properties = {}) {
  if (!app.tenantId || !navigator.onLine) return;
  try {
    await api("/api/v1/analytics/events", {
      method: "POST",
      body: { tenantId: app.tenantId, eventName, objectType, objectId: objectId == null ? null : String(objectId), properties, sessionRef: window.sessionStorage.getItem("aura.page.session") }
    });
  } catch { /* Product analytics never blocks the operator. */ }
}

async function loadIdentity() {
  const [identity, tenants] = await Promise.all([api("/api/auth/me"), api("/api/media-analysis/tenants")]);
  app.session = identity.data || {};
  app.tenants = Array.isArray(tenants.data) ? tenants.data : [];
  const select = $("#tenantScope");
  const stored = window.sessionStorage.getItem("aura.workbench.tenant");
  select.innerHTML = app.tenants.map((tenant) => `<option value="${tenant.tenantId}">${escapeHtml(`${tenant.tenantCode} · ${tenant.tenantName}`)}</option>`).join("");
  if (stored && Array.from(select.options).some((option) => option.value === stored)) select.value = stored;
  app.tenantId = Number(select.value) || null;
  applyPermissions();
}

async function loadEvents(page = app.eventPage) {
  const tenantId = requiredTenant();
  app.eventPage = page;
  const query = new URLSearchParams({ tenantId: String(tenantId), page: String(page), pageSize: String(app.eventPageSize) });
  if ($("#eventStatus").value) query.set("status", $("#eventStatus").value);
  if ($("#eventSeverity").value) query.set("severity", $("#eventSeverity").value);
  if ($("#eventKeyword").value.trim()) query.set("keyword", $("#eventKeyword").value.trim());
  const payload = await api(`/api/v1/events?${query}`);
  const rows = Array.isArray(payload.data) ? payload.data : [];
  $("#eventTotal").textContent = `${payload.pager?.total || 0} 条`;
  $("#eventRows").innerHTML = rows.length ? rows.map((item) => `<tr data-id="${item.eventId}" class="${app.selectedEvent?.eventId === item.eventId ? "is-selected" : ""}">
    <td class="wb-primary-cell"><strong>${escapeHtml(item.title)}</strong><span>${escapeHtml(item.eventNo)} · ${escapeHtml(item.eventType)}</span></td>
    <td>${severityBadge(item.severity)}</td><td>${stateBadge(item.status)}</td><td>${formatTime(item.lastOccurredAt)}</td>
  </tr>`).join("") : emptyRow(4);
  $("#eventRows").querySelectorAll("tr[data-id]").forEach((row) => row.addEventListener("click", () => openEvent(Number(row.dataset.id))));
  window.aura.renderPager($("#eventPager"), {
    page: payload.pager?.page || page,
    pageSize: payload.pager?.pageSize || app.eventPageSize,
    total: payload.pager?.total || 0,
    onChange: (nextPage, size) => { app.eventPageSize = size; loadEvents(nextPage).catch(handleError); }
  });
}

async function openEvent(eventId) {
  const tenantId = requiredTenant();
  const [detailPayload, timelinePayload] = await Promise.all([
    api(`/api/v1/events/${eventId}?tenantId=${tenantId}`),
    api(`/api/v1/events/${eventId}/timeline?tenantId=${tenantId}`)
  ]);
  const item = detailPayload.data;
  app.selectedEvent = item;
  const timeline = Array.isArray(timelinePayload.data) ? timelinePayload.data : [];
  $("#eventDetail").innerHTML = `<div class="wb-detail-head"><div><h2>${escapeHtml(item.title)}</h2><p>${escapeHtml(item.eventNo)} · v${item.version}</p></div>${severityBadge(item.severity)}</div>
    <div class="wb-detail-actions" data-permission="event.manage">
      ${item.status === "open" ? '<button class="btn-primary" type="button" data-event-action="acknowledge">确认</button>' : ""}
      ${item.status !== "dismissed" ? '<button class="btn-secondary" type="button" data-event-action="dismiss">排除</button>' : '<button class="btn-secondary" type="button" data-event-action="reopen">重开</button>'}
      <button class="btn-secondary" type="button" data-event-case>创建案件</button>
      ${item.status === "open" ? '<button class="btn-secondary" type="button" data-event-offline>保存确认草稿</button>' : ""}
    </div>
    <dl class="wb-facts"><div><dt>状态</dt><dd>${stateBadge(item.status)}</dd></div><div><dt>发生次数</dt><dd>${item.occurrenceCount}</dd></div><div><dt>实体</dt><dd>${escapeHtml(item.entityRef || "-")}</dd></div><div><dt>空间</dt><dd>${escapeHtml(item.spaceRef || "-")}</dd></div><div><dt>规则</dt><dd>${escapeHtml(item.ruleCode || "-")} ${item.ruleVersion ? `v${item.ruleVersion}` : ""}</dd></div><div><dt>模型</dt><dd>${escapeHtml(item.modelCode || "-")} ${escapeHtml(item.modelVersion || "")}</dd></div></dl>
    <div class="wb-section-head"><h2>活动</h2><span>${timeline.length}</span></div>${renderTimeline(timeline)}`;
  applyPermissions();
  $("#eventDetail").querySelectorAll("[data-event-action]").forEach((button) => button.addEventListener("click", () => transitionEvent(button.dataset.eventAction).catch(handleError)));
  $("#eventDetail").querySelector("[data-event-case]")?.addEventListener("click", () => {
    $("#caseCreate").elements.eventId.value = String(item.eventId);
    $("#caseCreate").elements.title.value = item.title;
    $("#caseDialog").showModal();
  });
  $("#eventDetail").querySelector("[data-event-offline]")?.addEventListener("click", () => queueEventDraft(item));
  loadEvents(app.eventPage).catch(() => {});
  recordAnalytics("event.detail.opened", "event", eventId);
}

function renderTimeline(rows) {
  if (!rows.length) return '<div class="wb-empty">暂无活动</div>';
  return `<ol class="wb-timeline">${rows.map((row) => {
    const detail = parseJson(row.detailJson);
    const summary = detail.content || detail.purpose || row.reasonCode || `${row.fromStatus || ""}${row.toStatus ? ` → ${row.toStatus}` : ""}` || "-";
    return `<li><strong>${escapeHtml(row.itemType)}</strong><span>${escapeHtml(summary)} · ${escapeHtml(row.actorName)} · ${formatTime(row.createdAt)}</span></li>`;
  }).join("")}</ol>`;
}

async function transitionEvent(action) {
  const item = app.selectedEvent;
  if (!item) return;
  const path = action === "acknowledge" ? "acknowledge" : action === "dismiss" ? "dismiss" : "reopen";
  try {
    await api(`/api/v1/events/${item.eventId}/${path}?tenantId=${requiredTenant()}`, {
      method: "POST",
      headers: { "Idempotency-Key": `workbench-${path}-${item.eventId}-${item.version}` },
      body: { expectedVersion: item.version, reasonCode: action === "dismiss" ? "operator_dismissed" : action === "reopen" ? "operator_reopened" : null, detail: { source: "workbench" } }
    });
    toast("事件状态已更新");
    await openEvent(item.eventId);
  } catch (error) {
    if (action === "acknowledge" && (!navigator.onLine || !error.status)) { queueEventDraft(item); return; }
    throw error;
  }
}

function queueEventDraft(item) {
  const rows = localDrafts();
  if (!rows.some((row) => row.objectType === "event" && row.objectId === String(item.eventId) && row.actionType === "event_acknowledge")) {
    rows.push({ clientDraftId: uuid(), tenantId: requiredTenant(), actionType: "event_acknowledge", objectType: "event", objectId: String(item.eventId), baseVersion: item.version, payload: {}, createdAt: new Date().toISOString() });
    saveLocalDrafts(rows);
  }
  toast("确认草稿已保存");
}

async function syncDrafts() {
  if (!navigator.onLine) { toast("当前离线", true); return; }
  const rows = localDrafts();
  const remaining = [];
  for (const draft of rows) {
    try {
      const saved = await api("/api/v1/mobile/drafts", {
        method: "POST",
        body: { tenantId: draft.tenantId, clientDraftId: draft.clientDraftId, actionType: draft.actionType, objectType: draft.objectType, objectId: draft.objectId, baseVersion: draft.baseVersion, payload: draft.payload, expiresAt: null }
      });
      await api(`/api/v1/mobile/drafts/${saved.data.mobileDraftId}/sync`, { method: "POST", body: { tenantId: draft.tenantId, currentVersion: null } });
    } catch (error) {
      draft.error = error.message;
      remaining.push(draft);
    }
  }
  saveLocalDrafts(remaining);
  toast(remaining.length ? `${remaining.length} 条草稿待处理` : "草稿同步完成", remaining.length > 0);
  if (app.activeTab === "events") await loadEvents();
}

async function loadCases(page = app.casePage) {
  const tenantId = requiredTenant();
  if (app.myTasks) {
    const payload = await api(`/api/v1/mobile/tasks?tenantId=${tenantId}`);
    const source = Array.isArray(payload.data?.cases) ? payload.data.cases : [];
    const status = $("#caseStatus").value;
    const keyword = $("#caseKeyword").value.trim().toLowerCase();
    const rows = source.filter((item) => (!status || item.status === status) && (!keyword || `${item.caseNo} ${item.title}`.toLowerCase().includes(keyword)));
    app.casePage = 1;
    $("#caseTotal").textContent = `${rows.length} 条待办`;
    $("#caseRows").innerHTML = rows.length ? rows.map((item) => `<tr data-id="${item.caseId}" class="${app.selectedCase?.caseId === item.caseId ? "is-selected" : ""}"><td class="wb-primary-cell"><strong>${escapeHtml(item.title)}</strong><span>${escapeHtml(item.caseNo)} · ${escapeHtml(item.priority || "-")}</span></td><td>${escapeHtml(item.priority || "-")}</td><td>${stateBadge(item.status)}</td><td>${formatTime(item.updatedAt)}</td></tr>`).join("") : emptyRow(4, "当前没有分配给你的案件");
    $("#caseRows").querySelectorAll("tr[data-id]").forEach((row) => row.addEventListener("click", () => openCase(Number(row.dataset.id))));
    $("#casePager").innerHTML = "";
    return;
  }
  app.casePage = page;
  const query = new URLSearchParams({ tenantId: String(tenantId), page: String(page), pageSize: String(app.casePageSize) });
  if ($("#caseStatus").value) query.set("status", $("#caseStatus").value);
  if ($("#caseKeyword").value.trim()) query.set("keyword", $("#caseKeyword").value.trim());
  const payload = await api(`/api/v1/cases?${query}`);
  const rows = Array.isArray(payload.data) ? payload.data : [];
  $("#caseTotal").textContent = `${payload.pager?.total || 0} 条`;
  $("#caseRows").innerHTML = rows.length ? rows.map((item) => `<tr data-id="${item.caseId}" class="${app.selectedCase?.caseId === item.caseId ? "is-selected" : ""}"><td class="wb-primary-cell"><strong>${escapeHtml(item.title)}</strong><span>${escapeHtml(item.caseNo)} · ${item.eventCount} 个事件</span></td><td>${escapeHtml(item.priority)}</td><td>${stateBadge(item.status)}</td><td>${formatTime(item.updatedAt)}</td></tr>`).join("") : emptyRow(4);
  $("#caseRows").querySelectorAll("tr[data-id]").forEach((row) => row.addEventListener("click", () => openCase(Number(row.dataset.id))));
  window.aura.renderPager($("#casePager"), { page: payload.pager?.page || page, pageSize: payload.pager?.pageSize || app.casePageSize, total: payload.pager?.total || 0, onChange: (nextPage, size) => { app.casePageSize = size; loadCases(nextPage).catch(handleError); } });
}

async function openCase(caseId) {
  const tenantId = requiredTenant();
  const [detailPayload, timelinePayload, participantsPayload, checklistPayload] = await Promise.all([
    api(`/api/v1/cases/${caseId}?tenantId=${tenantId}`),
    api(`/api/v1/cases/${caseId}/timeline?tenantId=${tenantId}`),
    api(`/api/v1/cases/${caseId}/participants?tenantId=${tenantId}`),
    api(`/api/v1/cases/${caseId}/checklist?tenantId=${tenantId}`)
  ]);
  const item = detailPayload.data;
  app.selectedCase = item;
  const timeline = Array.isArray(timelinePayload.data) ? timelinePayload.data : [];
  const participants = Array.isArray(participantsPayload.data) ? participantsPayload.data : [];
  const checklist = Array.isArray(checklistPayload.data) ? checklistPayload.data : [];
  $("#caseDetail").innerHTML = `<div class="wb-detail-head"><div><h2>${escapeHtml(item.title)}</h2><p>${escapeHtml(item.caseNo)} · v${item.version}</p></div>${stateBadge(item.status)}</div>
    <dl class="wb-facts"><div><dt>优先级</dt><dd>${escapeHtml(item.priority)}</dd></div><div><dt>负责人</dt><dd>${escapeHtml(item.ownerName || "未分配")}</dd></div><div><dt>关联事件</dt><dd>${item.eventCount}</dd></div><div><dt>证据</dt><dd>${item.evidenceCount}</dd></div><div><dt>解决期限</dt><dd>${formatTime(item.resolveDueAt)}</dd></div><div><dt>更新时间</dt><dd>${formatTime(item.updatedAt)}</dd></div></dl>
    <div class="wb-detail-actions"><button class="btn-secondary" id="copyCaseDeepLink" type="button">复制案件链接</button><output id="caseDeepLinkOutput" class="wb-muted"></output></div>
    <form class="wb-detail-form" id="caseTransition" data-permission="case.manage"><label><span>目标状态</span><select name="targetStatus"><option value="acknowledged">已确认</option><option value="in_progress">处理中</option><option value="paused">暂停</option><option value="escalated">升级</option><option value="resolved">解决</option><option value="closed">关闭</option><option value="reopened">重开</option><option value="false_positive">误报</option></select></label><label><span>原因</span><input name="reasonCode" required /></label><button class="btn-primary" type="submit">更新</button></form>
    <div class="wb-section-head"><h2>时间线</h2><span>${timeline.length}</span></div>${renderTimeline(timeline)}
    <form class="wb-detail-form" id="caseComment" data-permission="case.manage"><label><span>添加评论</span><input name="content" maxlength="4000" required /></label><button class="btn-secondary" type="submit">发送</button></form>
    <section class="wb-collaboration"><div class="wb-section-head"><h2>协作成员</h2><span>${participants.length}</span></div><ul class="wb-compact-list" id="caseParticipants">${participants.length ? participants.map((person) => `<li><span>${escapeHtml(person.userName || person.userId)}</span><small>${escapeHtml(person.roleType || "watcher")}</small></li>`).join("") : "<li class=\"wb-muted\">暂无协作成员</li>"}</ul><form class="wb-detail-form" id="caseParticipantForm" data-permission="case.manage"><label><span>用户 ID</span><input name="userId" type="number" min="1" required /></label><label><span>角色</span><select name="roleType"><option value="assignee">处置人</option><option value="coordinator">协调人</option><option value="watcher">关注人</option><option value="owner">负责人</option></select></label><button class="btn-secondary" type="submit">添加</button></form></section>
    <section class="wb-collaboration"><div class="wb-section-head"><h2>处置清单</h2><span>${checklist.length}</span></div><ul class="wb-checklist" id="caseChecklist">${checklist.length ? checklist.map((entry) => `<li><label><input type="checkbox" data-checklist-item="${entry.checklistItemId}" ${entry.status === "completed" ? "checked" : ""} /><span>${escapeHtml(entry.title || entry.itemCode || `项目 #${entry.checklistItemId}`)}</span></label></li>`).join("") : "<li class=\"wb-muted\">暂无清单项目，可先应用案件模板</li>"}</ul></section>
    <section class="wb-collaboration"><div class="wb-section-head"><h2>现场证据</h2></div><form class="wb-form" id="casePhotoForm" data-permission="case.manage" enctype="multipart/form-data"><label><span>照片</span><input name="file" type="file" accept="image/jpeg,image/png" capture="environment" required /></label><label><span>用途</span><input name="purpose" maxlength="256" value="现场核查" /></label><input name="latitude" type="hidden" /><input name="longitude" type="hidden" /><div class="wb-photo-actions"><button class="btn-secondary" type="button" id="captureLocation">记录当前位置</button><output id="caseLocation" class="wb-muted">未记录定位</output></div><button class="btn-primary" type="submit">上传照片</button></form></section>`;
  applyPermissions();
  $("#caseTransition")?.addEventListener("submit", submitCaseTransition);
  $("#caseComment")?.addEventListener("submit", submitCaseComment);
  $("#caseParticipantForm")?.addEventListener("submit", (event) => addCaseParticipant(event, caseId).catch(handleError));
  $("#caseChecklist")?.querySelectorAll("[data-checklist-item]").forEach((input) => input.addEventListener("change", (event) => updateCaseChecklist(event, caseId).catch(handleError)));
  $("#casePhotoForm")?.addEventListener("submit", (event) => uploadCasePhoto(event, caseId).catch(handleError));
  $("#captureLocation")?.addEventListener("click", captureLocation);
  $("#copyCaseDeepLink")?.addEventListener("click", () => copyCaseDeepLink(caseId).catch(handleError));
  loadCases(app.casePage).catch(() => {});
  recordAnalytics("case.detail.opened", "case", caseId);
}

async function submitCaseTransition(event) {
  event.preventDefault();
  const item = app.selectedCase;
  const values = formObject(event.currentTarget);
  await api(`/api/v1/cases/${item.caseId}/transitions?tenantId=${requiredTenant()}`, {
    method: "POST",
    headers: { "Idempotency-Key": `case-transition-${item.caseId}-${item.version}-${values.targetStatus}` },
    body: { targetStatus: values.targetStatus, expectedVersion: item.version, reasonCode: values.reasonCode, resolution: ["resolved", "closed", "false_positive"].includes(values.targetStatus) ? { summary: values.reasonCode } : null }
  });
  toast("案件状态已更新");
  await openCase(item.caseId);
}

async function submitCaseComment(event) {
  event.preventDefault();
  const values = formObject(event.currentTarget);
  try {
    await api(`/api/v1/cases/${app.selectedCase.caseId}/comments?tenantId=${requiredTenant()}`, { method: "POST", body: { content: values.content, visibility: "tenant" } });
  } catch (error) {
    if (!navigator.onLine || !error.status) {
      queueCaseComment(app.selectedCase, values.content);
      event.currentTarget.reset();
      return;
    }
    throw error;
  }
  event.currentTarget.reset();
  toast("评论已添加");
  await openCase(app.selectedCase.caseId);
}

function queueCaseComment(item, content) {
  const rows = localDrafts();
  rows.push({
    clientDraftId: uuid(), tenantId: requiredTenant(), actionType: "case_comment", objectType: "case",
    objectId: String(item.caseId), baseVersion: item.version, payload: { content }, createdAt: new Date().toISOString()
  });
  saveLocalDrafts(rows);
  toast("评论草稿已保存");
}

async function addCaseParticipant(event, caseId) {
  event.preventDefault();
  const values = formObject(event.currentTarget);
  await api(`/api/v1/cases/${caseId}/participants`, {
    method: "POST",
    body: { tenantId: requiredTenant(), userId: Number(values.userId), roleType: values.roleType }
  });
  toast("协作成员已添加");
  await openCase(caseId);
}

async function updateCaseChecklist(event, caseId) {
  const input = event.currentTarget;
  input.disabled = true;
  try {
    await api(`/api/v1/cases/${caseId}/checklist/${Number(input.dataset.checklistItem)}`, {
      method: "POST",
      body: { tenantId: requiredTenant(), status: input.checked ? "completed" : "pending", detail: { source: "workbench" } }
    });
    toast("处置清单已更新");
    await openCase(caseId);
  } catch (error) {
    input.checked = !input.checked;
    throw error;
  } finally {
    input.disabled = false;
  }
}

function captureLocation() {
  if (!window.isSecureContext || !("geolocation" in navigator)) {
    toast("当前浏览器环境不支持定位", true);
    return;
  }
  const output = $("#caseLocation");
  output.textContent = "正在获取定位";
  navigator.geolocation.getCurrentPosition((position) => {
    const form = $("#casePhotoForm");
    if (!form) return;
    form.elements.latitude.value = position.coords.latitude.toFixed(6);
    form.elements.longitude.value = position.coords.longitude.toFixed(6);
    output.textContent = `${form.elements.latitude.value}, ${form.elements.longitude.value}`;
  }, (error) => {
    output.textContent = "未记录定位";
    toast(error.message || "定位授权失败", true);
  }, { enableHighAccuracy: true, maximumAge: 30000, timeout: 12000 });
}

async function uploadCasePhoto(event, caseId) {
  event.preventDefault();
  const form = event.currentTarget;
  const file = form.elements.file.files?.[0];
  if (!file) throw new Error("请选择 JPEG 或 PNG 照片");
  const query = new URLSearchParams({ tenantId: String(requiredTenant()), purpose: form.elements.purpose.value || "现场核查" });
  if (form.elements.latitude.value && form.elements.longitude.value) {
    query.set("latitude", form.elements.latitude.value);
    query.set("longitude", form.elements.longitude.value);
  }
  const body = new FormData();
  body.append("file", file, file.name);
  const payload = await apiForm(`/api/v1/mobile/cases/${caseId}/photos?${query}`, body);
  toast(`照片已加入证据 #${payload.data.evidenceId}`);
  form.reset();
  $("#caseLocation").textContent = "未记录定位";
  await openCase(caseId);
}

async function copyCaseDeepLink(caseId) {
  const payload = await api("/api/v1/mobile/deep-links", {
    method: "POST", body: { tenantId: requiredTenant(), objectType: "case", objectId: String(caseId), reason: "现场协作" }
  });
  const absolute = new URL(payload.data.path, window.location.origin).toString();
  $("#caseDeepLinkOutput").textContent = absolute;
  if (navigator.clipboard && window.isSecureContext) {
    await navigator.clipboard.writeText(absolute);
    toast("案件链接已复制");
  } else {
    toast("案件链接已生成");
  }
}

async function createEvent(event) {
  event.preventDefault();
  const values = formObject(event.currentTarget);
  await api("/api/v1/events", {
    method: "POST",
    headers: { "Idempotency-Key": `event-create-${uuid()}` },
    body: { tenantId: requiredTenant(), eventType: values.eventType, title: values.title, summary: values.summary || null, severity: values.severity, aggregationKey: `manual:${uuid()}`, aggregationPolicyVersion: 1, occurredAt: new Date().toISOString(), ruleCode: null, ruleVersion: null, modelCode: null, modelVersion: null, entityRef: values.entityRef || null, spaceRef: values.spaceRef || null, representativeEvidence: {}, analysisEventId: null }
  });
  $("#eventDialog").close();
  event.currentTarget.reset();
  toast("事件已创建");
  await loadEvents(1);
}

async function createCase(event) {
  event.preventDefault();
  const values = formObject(event.currentTarget);
  await api("/api/v1/cases", {
    method: "POST",
    headers: { "Idempotency-Key": `case-create-${uuid()}` },
    body: { tenantId: requiredTenant(), title: values.title, description: values.description || null, priority: values.priority, ownerUserId: null, ownerName: null, eventIds: values.eventId ? [Number(values.eventId)] : [], tags: [], acknowledgeDueAt: null, startDueAt: null, resolveDueAt: null }
  });
  $("#caseDialog").close();
  event.currentTarget.reset();
  toast("案件已创建");
  await loadCases(1);
  if (app.activeTab === "events") await loadEvents();
}

async function createInvestigation(event) {
  event.preventDefault();
  const values = formObject(event.currentTarget);
  const payload = await api("/api/v1/investigations", { method: "POST", body: { tenantId: requiredTenant(), title: values.title } });
  app.investigationId = payload.data.investigationId;
  event.currentTarget.reset();
  await openInvestigation(app.investigationId);
  toast("调查已创建");
}

async function openInvestigation(investigationId) {
  const payload = await api(`/api/v1/investigations/${investigationId}?tenantId=${requiredTenant()}`);
  app.investigationId = investigationId;
  app.controlledPlan = null;
  const session = payload.data.session;
  $("#investigationWorkspace").hidden = false;
  $("#controlledPlan").hidden = true;
  $("#investigationTitle").textContent = session.title;
  $("#investigationMeta").textContent = `${session.investigationNo} · ${session.status}`;
  $("#investigationOutput").textContent = JSON.stringify(payload.data, null, 2);
}

async function runInvestigationQuery(event) {
  event.preventDefault();
  if (!app.investigationId) throw new Error("请先打开调查");
  const values = formObject(event.currentTarget);
  const payload = await api(`/api/v1/investigations/${app.investigationId}/queries?tenantId=${requiredTenant()}`, { method: "POST", body: { queryType: values.queryType, query: parseJson(values.query), modelCode: null, modelVersion: null, thresholdPolicyVersion: null, dataVersion: "current" } });
  $("#investigationOutput").textContent = JSON.stringify(payload.data, null, 2);
}

async function createControlledQuery(event) {
  event.preventDefault();
  if (!app.investigationId) throw new Error("请先打开调查");
  const values = formObject(event.currentTarget);
  const payload = await api("/api/v1/controlled-queries", { method: "POST", body: { tenantId: requiredTenant(), investigationId: app.investigationId, text: values.text } });
  app.controlledPlan = payload.data;
  renderControlledPlan(payload.data);
  $("#investigationOutput").textContent = "查询计划已生成。修改结构化条件后，需要显式确认才能执行。";
}

function renderControlledPlan(data) {
  const host = $("#controlledPlan");
  host.hidden = false;
  host.innerHTML = `<div class="wb-section-head"><h2>待确认查询计划</h2>${stateBadge(data.status || "pending_confirmation")}</div><label><span>结构化计划</span><textarea id="controlledPlanJson" rows="13" spellcheck="false">${escapeHtml(JSON.stringify(data.plan || {}, null, 2))}</textarea></label><div class="wb-actions"><button class="btn-secondary" id="rejectControlledPlan" type="button">拒绝</button><button class="btn-primary" id="executeControlledPlan" type="button">保存、确认并执行</button></div>`;
  $("#rejectControlledPlan").addEventListener("click", () => rejectControlledPlan().catch(handleError));
  $("#executeControlledPlan").addEventListener("click", () => executeControlledPlan().catch(handleError));
}

async function rejectControlledPlan() {
  if (!app.controlledPlan?.queryPlanId) throw new Error("没有待确认的查询计划");
  await api(`/api/v1/controlled-queries/${app.controlledPlan.queryPlanId}/confirm`, {
    method: "POST", body: { tenantId: requiredTenant(), confirm: false }
  });
  $("#controlledPlan").hidden = true;
  app.controlledPlan = null;
  $("#investigationOutput").textContent = "查询计划已拒绝，未执行任何查询。";
}

async function executeControlledPlan() {
  if (!app.controlledPlan?.queryPlanId) throw new Error("没有待确认的查询计划");
  let plan;
  try { plan = JSON.parse($("#controlledPlanJson").value); }
  catch { throw new Error("结构化计划不是有效 JSON"); }
  const queryPlanId = app.controlledPlan.queryPlanId;
  const updated = await api(`/api/v1/controlled-queries/${queryPlanId}/plan`, {
    method: "PUT", body: { tenantId: requiredTenant(), plan }
  });
  await api(`/api/v1/controlled-queries/${queryPlanId}/confirm`, {
    method: "POST", body: { tenantId: requiredTenant(), confirm: true }
  });
  const result = await api(`/api/v1/controlled-queries/${queryPlanId}/execute?tenantId=${requiredTenant()}`, { method: "POST" });
  $("#controlledPlan").hidden = true;
  app.controlledPlan = { ...updated.data, status: "executed" };
  $("#investigationOutput").textContent = JSON.stringify(result.data, null, 2);
  toast("查询计划已确认并执行");
}

async function loadOnboarding() {
  const payload = await api(`/api/v1/integrations/onboarding?tenantId=${requiredTenant()}&page=1&pageSize=100`);
  const rows = Array.isArray(payload.data) ? payload.data : [];
  $("#onboardingRows").innerHTML = rows.length ? rows.map((item) => `<tr><td>${item.onboardingId}</td><td>${escapeHtml(item.name)}</td><td>${escapeHtml(item.integrationType)}</td><td>${item.currentStep}/7</td><td>${stateBadge(item.status)}</td><td>${formatTime(item.updatedAt)}</td></tr>`).join("") : emptyRow(6);
}

async function createOnboarding(event) {
  event.preventDefault();
  const values = formObject(event.currentTarget);
  const payload = await api("/api/v1/integrations/onboarding", { method: "POST", body: { tenantId: requiredTenant(), integrationType: values.integrationType, name: values.name } });
  $("#onboardingStep").elements.onboardingId.value = payload.data.onboardingId;
  event.currentTarget.reset();
  toast("接入向导已创建");
  await loadOnboarding();
}

async function saveOnboardingStep(event) {
  event.preventDefault();
  const values = formObject(event.currentTarget);
  await api(`/api/v1/integrations/onboarding/${values.onboardingId}/steps?tenantId=${requiredTenant()}`, { method: "POST", body: { step: Number(values.step), config: parseJson(values.config), secretReferences: null, runTest: values.runTest === "on", exemptionReason: null } });
  toast("向导步骤已保存");
  await loadOnboarding();
}

async function loadRules() {
  const payload = await api(`/api/v1/governance/rules?tenantId=${requiredTenant()}&limit=200`);
  const rows = Array.isArray(payload.data) ? payload.data : [];
  $("#ruleRows").innerHTML = rows.length ? rows.map((item) => `<tr><td class="wb-primary-cell"><strong>${escapeHtml(item.name)}</strong><span>${escapeHtml(item.rule_code)} · #${item.rule_id}</span></td><td>${stateBadge(item.status)}</td><td>${item.active_version || "-"}</td><td>${item.tripped_at ? stateBadge("critical") : "-"}</td><td>${formatTime(item.updated_at)}</td><td><button class="btn-secondary" type="button" data-rule-dry="${item.rule_id}" data-version="${item.active_version || 1}">试运行</button> <button class="btn-secondary" type="button" data-rule-submit="${item.rule_id}">送审</button></td></tr>`).join("") : emptyRow(6);
  $("#ruleRows").querySelectorAll("[data-rule-dry]").forEach((button) => button.addEventListener("click", () => dryRunRule(Number(button.dataset.ruleDry), Number(button.dataset.version)).catch(handleError)));
  $("#ruleRows").querySelectorAll("[data-rule-submit]").forEach((button) => button.addEventListener("click", () => transitionRule(Number(button.dataset.ruleSubmit), "pending_approval").catch(handleError)));
}

async function createRule(event) {
  event.preventDefault();
  const values = formObject(event.currentTarget);
  const created = await api("/api/v1/governance/rules", { method: "POST", body: { tenantId: requiredTenant(), payload: { ruleCode: values.ruleCode, name: values.name } } });
  const ruleId = created.data.id;
  await api("/api/v1/governance/rule-versions", { method: "POST", body: { tenantId: requiredTenant(), payload: { ruleId, condition: { eventType: values.eventType || null, occurrenceMin: Number(values.occurrenceMin), startHour: Number(values.startHour), endHour: Number(values.endHour) }, action: { createCase: values.createCase === "on" }, noiseControl: { suppressionMinutes: Number(values.suppressionMinutes), maxTriggersPerHour: Number(values.maxTriggersPerHour), keyBy: "entity" }, rollout: { shadow: values.shadow === "on", percentage: 100 } } } });
  $("#ruleDialog").close();
  event.currentTarget.reset();
  toast("规则草稿已保存");
  await loadRules();
}

async function dryRunRule(ruleId, version) {
  const payload = await api(`/api/v1/rules/${ruleId}/dry-run`, { method: "POST", body: { tenantId: requiredTenant(), version, from: null, to: null, limit: 10000 } });
  toast(`试运行命中 ${payload.data?.matchedCount ?? 0} 条`);
  await loadRules();
}

async function transitionRule(ruleId, targetStatus) {
  await api(`/api/v1/governance/rules/${ruleId}/transitions`, { method: "POST", body: { tenantId: requiredTenant(), targetStatus, reason: "workbench_review" } });
  toast("规则已送审");
  await loadRules();
}

async function loadAi() {
  const days = Number($("#timeWindow").value || 30);
  const to = new Date();
  const from = new Date(to.getTime() - days * 86400000);
  const payload = await api(`/api/v1/ai-governance/dashboard?tenantId=${requiredTenant()}&from=${encodeURIComponent(from.toISOString())}&to=${encodeURIComponent(to.toISOString())}`);
  const rows = Array.isArray(payload.data.models) ? payload.data.models : [];
  const evaluationCount = rows.reduce((sum, row) => sum + Number(row.evaluationCount || 0), 0);
  const feedbackCount = rows.reduce((sum, row) => sum + Number(row.feedbackCount || 0), 0);
  const driftCount = rows.filter((row) => row.driftStatus && row.driftStatus !== "normal").length;
  $("#aiMetrics").innerHTML = metricsHtml([{ label: "模型版本", value: rows.length }, { label: "评测运行", value: evaluationCount }, { label: "人工反馈", value: feedbackCount }, { label: "漂移告警", value: driftCount, tone: driftCount ? "danger" : "success" }]);
  $("#aiRows").innerHTML = rows.length ? rows.map((row) => `<tr><td>${escapeHtml(row.modelCode)}<br><small>${escapeHtml(row.modelVersion)}</small></td><td>${stateBadge(row.status)}</td><td>${row.passedCount}/${row.evaluationCount}</td><td>${row.negativeFeedbackCount}/${row.feedbackCount}</td><td>${stateBadge(row.driftStatus || "unknown")}</td></tr>`).join("") : emptyRow(5);
}

async function submitAiFeedback(event) {
  event.preventDefault();
  const values = formObject(event.currentTarget);
  await api("/api/v1/governance/ai-feedback", { method: "POST", body: { tenantId: requiredTenant(), payload: { objectType: values.objectType, objectId: values.objectId, modelReleaseId: null, feedbackType: values.feedbackType, reasonCode: values.reasonCode, note: null } } });
  event.currentTarget.reset();
  toast("反馈已提交");
  await loadAi();
}

async function loadGovernance() {
  const resource = $("#governanceResource").value;
  const payload = await api(`/api/v1/governance/${resource}?tenantId=${requiredTenant()}&limit=200`);
  $("#governanceOutput").textContent = JSON.stringify(payload.data, null, 2);
}

function metricsHtml(items) {
  return items.map((item) => `<div class="wb-metric" data-tone="${item.tone || "default"}"><span>${escapeHtml(item.label)}</span><strong>${escapeHtml(String(item.value ?? "-"))}</strong></div>`).join("");
}

async function loadOps() {
  const payload = await api(`/api/v1/ops/center?tenantId=${requiredTenant()}`);
  const data = payload.data;
  const workloads = data.workloads || {};
  $("#opsMetrics").innerHTML = metricsHtml([
    { label: "待分诊事件", value: workloads.events?.open || 0, tone: workloads.events?.open ? "warn" : "success" },
    { label: "在办案件", value: workloads.cases?.active || 0 },
    { label: "超期案件", value: workloads.cases?.overdue || 0, tone: workloads.cases?.overdue ? "danger" : "success" },
    { label: "通知失败", value: workloads.notifications?.failed || 0, tone: workloads.notifications?.failed ? "danger" : "success" },
    { label: "发布控制", value: data.slo?.releaseControl?.state || "normal", tone: data.slo?.releaseControl?.state === "normal" ? "success" : "danger" }
  ]);
  const ready = data.readiness || {};
  const components = [
    { name: "PostgreSQL / pgvector", status: ready.pgvector?.extensionAvailable && ready.pgvector?.tableAvailable ? "ready" : "failed", backlog: ready.pgvector?.vectorCount, last: "-" },
    { name: "Inbox", status: ready.inbox?.deadLetterCount ? "failed" : "ready", backlog: ready.inbox?.pendingCount, last: "-" },
    { name: "Outbox", status: ready.outbox?.deadLetterCount ? "failed" : "ready", backlog: ready.outbox?.pendingCount, last: "-" },
    { name: "ArangoDB", status: !ready.graph?.enabled ? "disabled" : ready.graph?.available ? "ready" : "failed", backlog: "-", last: ready.graph?.version || "-" },
    ...(Array.isArray(ready.workers) ? ready.workers.map((worker) => ({ name: worker.workerName, status: worker.healthy ? "ready" : worker.status, backlog: "-", last: worker.lastSuccessAt })) : [])
  ];
  $("#healthRows").innerHTML = components.map((item) => `<tr><td>${escapeHtml(item.name)}</td><td>${stateBadge(item.status)}</td><td>${escapeHtml(String(item.backlog ?? "-"))}</td><td>${formatTime(item.last)}</td></tr>`).join("");
}

async function previewHighRisk(event) {
  event.preventDefault();
  const values = formObject(event.currentTarget);
  const payload = await api("/api/v1/ops/high-risk/preview", { method: "POST", headers: { "Idempotency-Key": `high-risk-${uuid()}` }, body: { tenantId: requiredTenant(), operationType: values.operationType, scope: parseJson(values.scope), requestedBatchSize: 100, ticketNo: values.ticketNo } });
  const task = await api(`/api/v1/ops/high-risk/${payload.data.taskId}`);
  app.highRisk = { ...payload.data, version: task.data.version, ticketNo: values.ticketNo };
  $("#highRiskOutput").textContent = JSON.stringify(app.highRisk, null, 2);
  $("#highRiskConfirm").hidden = false;
  $("#highRiskConfirm").elements.confirmationPhrase.value = "";
}

async function executeHighRisk(event) {
  event.preventDefault();
  if (!app.highRisk) return;
  const values = formObject(event.currentTarget);
  const payload = await api(`/api/v1/ops/high-risk/${app.highRisk.taskId}/execute`, { method: "POST", body: { confirmationPhrase: values.confirmationPhrase, ticketNo: app.highRisk.ticketNo, stepUpVerified: true, expectedVersion: app.highRisk.version } });
  $("#highRiskOutput").textContent = JSON.stringify(payload.data, null, 2);
  event.currentTarget.hidden = true;
  app.highRisk = null;
  toast("高影响任务已排队");
}

async function loadAnalytics() {
  const days = Number($("#timeWindow").value || 30);
  const to = new Date();
  const from = new Date(to.getTime() - days * 86400000);
  const payload = await api(`/api/v1/analytics/dashboard?tenantId=${requiredTenant()}&from=${encodeURIComponent(from.toISOString())}&to=${encodeURIComponent(to.toISOString())}`);
  const data = payload.data;
  const summary = data.summary || {};
  const rates = data.rates || {};
  const timing = data.caseTiming || {};
  const quality = data.quality || {};
  const aiQuality = data.aiQuality || {};
  const platform = data.platform || {};
  const usage = Array.isArray(data.resources?.usage) ? data.resources.usage : [];
  const costs = Array.isArray(data.resources?.costs) ? data.resources.costs : [];
  $("#businessMetrics").innerHTML = metricsHtml([
    { label: "事件", value: summary.eventCount || 0 },
    { label: "独立事件", value: summary.independentEventCount || 0 },
    { label: "重复发生", value: summary.repeatedEventCount || 0, tone: summary.repeatedEventCount ? "warn" : "success" },
    { label: "案件", value: summary.caseCount || 0 },
    { label: "闭环案件", value: summary.closedCaseCount || 0, tone: "success" },
    { label: "当前积压", value: summary.openBacklog || 0, tone: summary.openBacklog ? "warn" : "success" },
    { label: "SLA 达标率", value: formatRate(rates.slaCompliance) },
    { label: "平均解决", value: formatDuration(timing.averageCloseSeconds) }
  ]);
  $("#qualityMetrics").innerHTML = metricsHtml([
    { label: "证据完整率", value: formatRate(rates.evidenceCompleteness) },
    { label: "重开率", value: formatRate(rates.reopenRate), tone: Number(quality.reopenedCaseCount || 0) ? "warn" : "success" },
    { label: "误报反馈率", value: formatRate(rates.falsePositiveFeedback) },
    { label: "重复案件", value: quality.duplicateCaseCount || 0 },
    { label: "复核通过", value: `${quality.acceptedFeedbackCount || 0}/${quality.reviewedFeedbackCount || 0}` },
    { label: "平均确认", value: formatDuration(timing.averageAcknowledgeSeconds) }
  ]);
  $("#aiQualityMetrics").innerHTML = metricsHtml([
    { label: "评测通过", value: `${aiQuality.passedEvaluationCount || 0}/${aiQuality.evaluationCount || 0}` },
    { label: "空结果", value: `${aiQuality.emptyResultCount || 0}/${aiQuality.evaluationItemCount || 0}` },
    { label: "漂移告警", value: aiQuality.driftAlertCount || 0, tone: aiQuality.driftAlertCount ? "danger" : "success" },
    { label: "人工反馈", value: summary.feedbackCount || 0 }
  ]);
  $("#platformMetrics").innerHTML = metricsHtml([
    { label: "提供方成功率", value: formatRate(rates.providerJobSuccess) },
    { label: "任务积压", value: platform.providerJobBacklog || 0, tone: platform.providerJobBacklog ? "warn" : "success" },
    { label: "死信", value: Number(platform.outboxDeadLetters || 0) + Number(platform.inboxDeadLetters || 0), tone: platform.outboxDeadLetters || platform.inboxDeadLetters ? "danger" : "success" },
    { label: "通知失败", value: platform.notificationFailures || 0, tone: platform.notificationFailures ? "danger" : "success" },
    { label: "计量项目", value: usage.length },
    { label: "成本项目", value: costs.length }
  ]);
  renderTrend(Array.isArray(data.trend) ? data.trend : []);
  const hotSpots = Array.isArray(data.hotSpots) ? data.hotSpots : [];
  $("#hotSpotRows").innerHTML = hotSpots.length ? hotSpots.map((row) => `<tr><td>${escapeHtml(row.spaceRef)}</td><td>${row.eventCount}</td><td>${row.highSeverityCount}</td></tr>`).join("") : emptyRow(3);
  const definitions = Array.isArray(data.metricDefinitions) ? data.metricDefinitions : [];
  $("#metricDefinitionRows").innerHTML = definitions.length ? definitions.map((row) => `<tr><td>${escapeHtml(row.name)}<br><small>${escapeHtml(row.metricCode)} v${row.version}</small></td><td>${escapeHtml(row.numeratorDefinition)}</td><td>${escapeHtml(row.denominatorDefinition)}</td><td>${escapeHtml(row.windowDefinition)}</td></tr>`).join("") : emptyRow(4);
}

function formatRate(rate) {
  if (!rate) return "-";
  const numerator = Number(rate.numerator || 0);
  const denominator = Number(rate.denominator || 0);
  const value = rate.value == null ? "-" : `${(Number(rate.value) * 100).toFixed(1)}%`;
  return `${value} (${numerator}/${denominator})`;
}

function formatDuration(seconds) {
  const value = Number(seconds || 0);
  if (!value) return "-";
  if (value < 60) return `${Math.round(value)} 秒`;
  if (value < 3600) return `${(value / 60).toFixed(1)} 分钟`;
  return `${(value / 3600).toFixed(1)} 小时`;
}

function renderTrend(rows) {
  const max = Math.max(1, ...rows.map((row) => Number(row.eventCount || 0) + Number(row.caseCount || 0)));
  $("#businessTrend").innerHTML = rows.map((row) => {
    const count = Number(row.eventCount || 0) + Number(row.caseCount || 0);
    const height = Math.max(4, Math.round(count / max * 200));
    return `<button type="button" class="wb-trend-bar" style="--height:${height}px" data-count="${count}" title="${escapeHtml(formatTime(row.day))}: ${count}" aria-label="${escapeHtml(formatTime(row.day))} 共 ${count}"></button>`;
  }).join("");
}

async function loadActiveView() {
  const loaders = { events: loadEvents, cases: loadCases, integration: loadOnboarding, governance: loadRules, operations: loadOps, analytics: loadAnalytics };
  const loader = loaders[app.activeTab];
  if (loader) await loader();
}

function activateTab(name) {
  app.activeTab = name;
  $$(".wb-tab").forEach((button) => button.classList.toggle("is-active", button.dataset.tab === name));
  $$(".wb-view").forEach((view) => { const active = view.dataset.view === name; view.classList.toggle("is-active", active); view.hidden = !active; });
  window.sessionStorage.setItem("aura.workbench.tab", name);
  loadActiveView().catch(handleError);
  recordAnalytics("workbench.tab.opened", "view", name);
}

function bindNavigation() {
  $$(".wb-tab").forEach((button) => button.addEventListener("click", () => activateTab(button.dataset.tab)));
  $$('[data-governance-view]').forEach((button) => button.addEventListener("click", () => {
    const name = button.dataset.governanceView;
    $$('[data-governance-view]').forEach((item) => item.classList.toggle("is-active", item === button));
    $$('[data-governance-panel]').forEach((panel) => { panel.hidden = panel.dataset.governancePanel !== name; });
    if (name === "ai") loadAi().catch(handleError);
    if (name === "data") loadGovernance().catch(handleError);
  }));
}

function bindForms() {
  $("#searchEvents").addEventListener("click", () => loadEvents(1).catch(handleError));
  $("#searchCases").addEventListener("click", () => loadCases(1).catch(handleError));
  $("#loadMyTasks").addEventListener("click", () => {
    app.myTasks = !app.myTasks;
    $("#loadMyTasks").classList.toggle("is-active", app.myTasks);
    $("#loadMyTasks").setAttribute("aria-pressed", String(app.myTasks));
    loadCases(1).catch(handleError);
  });
  $("#enablePush").addEventListener("click", () => enablePushNotifications().catch(handleError));
  $("#scanDeepLink").addEventListener("click", () => $("#deepLinkFile").click());
  $("#deepLinkFile").addEventListener("change", (event) => scanDeepLink(event).catch(handleError));
  $("#eventKeyword").addEventListener("keydown", (event) => { if (event.key === "Enter") loadEvents(1).catch(handleError); });
  $("#caseKeyword").addEventListener("keydown", (event) => { if (event.key === "Enter") loadCases(1).catch(handleError); });
  $("#openEventCreate").addEventListener("click", () => $("#eventDialog").showModal());
  $("#openCaseCreate").addEventListener("click", () => $("#caseDialog").showModal());
  $("#openRuleCreate").addEventListener("click", () => $("#ruleDialog").showModal());
  document.querySelectorAll("[data-close-dialog]").forEach((button) => {
    button.addEventListener("click", () => button.closest("dialog")?.close());
  });
  $("#eventCreate").addEventListener("submit", (event) => createEvent(event).catch(handleError));
  $("#caseCreate").addEventListener("submit", (event) => createCase(event).catch(handleError));
  $("#ruleCreate").addEventListener("submit", (event) => createRule(event).catch(handleError));
  $("#investigationCreate").addEventListener("submit", (event) => createInvestigation(event).catch(handleError));
  $("#investigationOpen").addEventListener("submit", (event) => { event.preventDefault(); openInvestigation(Number(formObject(event.currentTarget).investigationId)).catch(handleError); });
  $("#investigationQuery").addEventListener("submit", (event) => runInvestigationQuery(event).catch(handleError));
  $("#controlledQuery").addEventListener("submit", (event) => createControlledQuery(event).catch(handleError));
  $("#onboardingCreate").addEventListener("submit", (event) => createOnboarding(event).catch(handleError));
  $("#onboardingStep").addEventListener("submit", (event) => saveOnboardingStep(event).catch(handleError));
  $("#loadOnboarding").addEventListener("click", () => loadOnboarding().catch(handleError));
  $("#loadRules").addEventListener("click", () => loadRules().catch(handleError));
  $("#loadAi").addEventListener("click", () => loadAi().catch(handleError));
  $("#aiFeedback").addEventListener("submit", (event) => submitAiFeedback(event).catch(handleError));
  $("#loadGovernance").addEventListener("click", () => loadGovernance().catch(handleError));
  $("#loadOps").addEventListener("click", () => loadOps().catch(handleError));
  $("#highRiskPreview").addEventListener("submit", (event) => previewHighRisk(event).catch(handleError));
  $("#highRiskConfirm").addEventListener("submit", (event) => executeHighRisk(event).catch(handleError));
  $("#tenantScope").addEventListener("change", async (event) => { app.tenantId = Number(event.currentTarget.value) || null; window.sessionStorage.setItem("aura.workbench.tenant", event.currentTarget.value); app.selectedEvent = null; app.selectedCase = null; try { await loadActiveView(); } catch (error) { handleError(error); } });
  $("#timeWindow").addEventListener("change", () => { if (app.activeTab === "analytics") loadAnalytics().catch(handleError); });
  $("#refreshView").addEventListener("click", () => loadActiveView().catch(handleError));
  $("#syncDrafts").addEventListener("click", () => syncDrafts().catch(handleError));
}

function base64UrlToUint8Array(value) {
  const padding = "=".repeat((4 - value.length % 4) % 4);
  const base64 = (value + padding).replaceAll("-", "+").replaceAll("_", "/");
  return Uint8Array.from(window.atob(base64), (character) => character.charCodeAt(0));
}

function arrayBufferToBase64Url(value) {
  const bytes = new Uint8Array(value);
  let binary = "";
  bytes.forEach((byte) => { binary += String.fromCharCode(byte); });
  return window.btoa(binary).replaceAll("+", "-").replaceAll("/", "_").replaceAll("=", "");
}

async function enablePushNotifications() {
  if (!("serviceWorker" in navigator) || !("PushManager" in window) || !window.isSecureContext)
    throw new Error("当前浏览器环境不支持安全推送");
  let publicKey = window.AURA_PAGE_CONFIG?.webPushPublicKey || window.AURA_WEB_PUSH_PUBLIC_KEY || "";
  if (!publicKey) {
    const config = await api("/api/v1/mobile/push-config");
    publicKey = config.data?.publicKey || "";
  }
  if (!publicKey) throw new Error("部署尚未配置 Web Push 公钥");
  const permission = await window.Notification.requestPermission();
  if (permission !== "granted") throw new Error("通知权限未授予");
  const registration = await navigator.serviceWorker.ready;
  const subscription = await registration.pushManager.getSubscription() || await registration.pushManager.subscribe({
    userVisibleOnly: true,
    applicationServerKey: base64UrlToUint8Array(publicKey)
  });
  const key = subscription.getKey("p256dh");
  const auth = subscription.getKey("auth");
  if (!key || !auth) throw new Error("浏览器没有返回完整推送密钥");
  await api("/api/v1/mobile/push-subscriptions", {
    method: "POST",
    body: {
      tenantId: requiredTenant(), endpointUri: subscription.endpoint,
      keyP256dh: arrayBufferToBase64Url(key), keyAuth: arrayBufferToBase64Url(auth), userAgent: navigator.userAgent
    }
  });
  $("#enablePush").textContent = "推送已启用";
  $("#enablePush").disabled = true;
  toast("案件推送已启用");
}

async function scanDeepLink(event) {
  const file = event.currentTarget.files?.[0];
  event.currentTarget.value = "";
  if (!file) return;
  if (!("BarcodeDetector" in window) || !("createImageBitmap" in window))
    throw new Error("当前浏览器不支持二维码识别");
  const detector = new window.BarcodeDetector({ formats: ["qr_code"] });
  const bitmap = await window.createImageBitmap(file);
  let codes;
  try { codes = await detector.detect(bitmap); }
  finally { bitmap.close(); }
  const value = codes[0]?.rawValue;
  if (!value) throw new Error("照片中未识别到二维码");
  const target = new URL(value, window.location.origin);
  if (target.origin !== window.location.origin || target.pathname.replace(/\/+$/, "/") !== "/workbench/")
    throw new Error("二维码不是 Aura 工作台深链");
  window.location.assign(target.toString());
}

function readDeepLinkContext() {
  const query = new URLSearchParams(window.location.search);
  const tenantId = Number(query.get("tenantId"));
  if (tenantId && Array.from($("#tenantScope").options).some((option) => Number(option.value) === tenantId)) {
    $("#tenantScope").value = String(tenantId);
    app.tenantId = tenantId;
    window.sessionStorage.setItem("aura.workbench.tenant", String(tenantId));
  }
  const allowedTabs = new Set(["events", "cases", "investigation", "integration", "governance", "operations", "analytics"]);
  const tab = allowedTabs.has(query.get("tab")) ? query.get("tab") : null;
  const positiveId = (name) => {
    const value = Number(query.get(name));
    return Number.isSafeInteger(value) && value > 0 ? value : null;
  };
  return { tab, caseId: positiveId("caseId"), eventId: positiveId("eventId"), investigationId: positiveId("investigationId") };
}

async function openDeepLinkTarget(context) {
  if (context.caseId) await openCase(context.caseId);
  else if (context.eventId) await openEvent(context.eventId);
  else if (context.investigationId) await openInvestigation(context.investigationId);
}

function handleError(error) {
  if (error?.status === 401) { window.location.href = "/login/"; return; }
  toast(error?.message || "操作失败", true);
}

async function registerServiceWorker() {
  if ("serviceWorker" in navigator && window.isSecureContext) {
    try { await navigator.serviceWorker.register("./sw.js", { scope: "/workbench/" }); } catch { /* PWA remains usable without installation. */ }
  }
}

async function initialize() {
  bindNavigation();
  bindForms();
  updateConnectionState();
  updateDraftCount();
  window.addEventListener("online", () => { updateConnectionState(); syncDrafts().catch(() => {}); });
  window.addEventListener("offline", updateConnectionState);
  await loadIdentity();
  const deepLink = readDeepLinkContext();
  const savedTab = window.sessionStorage.getItem("aura.workbench.tab");
  if (deepLink.tab) app.activeTab = deepLink.tab;
  else if (savedTab && $(`.wb-tab[data-tab="${savedTab}"]`)) app.activeTab = savedTab;
  activateTab(app.activeTab);
  await registerServiceWorker();
  await openDeepLinkTarget(deepLink);
}

initialize().catch(handleError);
