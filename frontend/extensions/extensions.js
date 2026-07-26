/* 文件：扩展管理页脚本（extensions.js） */
(function () {
  const apiBase = "";
  const fallbackRequestJson = async (url, options = {}) => {
    const headers = new Headers(options.headers || {});
    const init = { ...options, credentials: options.credentials || "include", headers };
    if (options.body && typeof options.body === "object" && !(options.body instanceof FormData)) {
      headers.set("Content-Type", headers.get("Content-Type") || "application/json");
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
  };

  const requestJson = window.aura?.requestJson || fallbackRequestJson;
  const escapeHtml = window.aura?.escapeHtml || ((value) =>
    String(value ?? "")
      .replaceAll("&", "&amp;")
      .replaceAll("<", "&lt;")
      .replaceAll(">", "&gt;")
      .replaceAll('"', '&quot;')
      .replaceAll("'", "&#39;")
  );
  const formatDateTime = window.aura?.formatDateTime || window.formatDateTimeDisplay || ((value) => String(value ?? "—"));
  const status = window.aura?.createStatusController?.(document.getElementById("extensionsStatus"), { successMs: 3200 });
  const tableWrap = document.getElementById("extensionsTableWrap");
  const tableHead = document.getElementById("extensionsTableHead");
  const tableBody = document.getElementById("extensionsTableBody");
  const modal = document.getElementById("extensionsModal");
  const modalTitle = document.getElementById("extensionsModalTitle");
  const formEl = document.getElementById("extensionsForm");
  const saveBtn = document.getElementById("saveExtension");
  const refreshBtn = document.getElementById("refreshSection");
  const openCreateBtn = document.getElementById("openCreateModal");
  const openAiExperimentBtn = document.getElementById("openAiExperimentModal");
  const generateReportBtn = document.getElementById("generateReport");
  const openTenantScopeBtn = document.getElementById("openTenantScopeModal");
  let activeSection = "workflow";
  let mode = "workflow";
  let allowedSections = [];

  const field = (name, label, attrs = "") => `<label><span>${escapeHtml(label)}</span><input class="aura-input" name="${escapeHtml(name)}" ${attrs} /></label>`;
  const textarea = (name, label, attrs = "") => `<label class="extensions-wide-field"><span>${escapeHtml(label)}</span><textarea class="aura-input" name="${escapeHtml(name)}" ${attrs}></textarea></label>`;
  const select = (name, label, options) =>
    `<label><span>${escapeHtml(label)}</span><select class="aura-input" name="${escapeHtml(name)}">${options
      .map((item) => `<option value="${escapeHtml(item.value)}">${escapeHtml(item.label)}</option>`)
      .join("")}</select></label>`;

  const sections = {
    workflow: {
      title: "告警闭环",
      permission: "alert.manage",
      createTitle: "更新告警闭环",
      listUrl: "/api/alert/workflow/list?limit=100",
      postUrl: (values) => `/api/alert/${encodeURIComponent(values.alertId || "")}/workflow`,
      columns: ["流程ID", "告警ID", "状态", "处理人", "优先级", "升级", "交接人", "更新时间"],
      row: (item) => [
        item.workflowId,
        item.alertId,
        item.status,
        item.assignee,
        item.priority,
        item.escalationLevel,
        item.handoverTo,
        formatDateTime(item.updatedAt)
      ],
      form: () =>
        [
          field("alertId", "告警ID", 'type="number" min="1" required'),
          select("status", "状态", [
            { value: "acknowledged", label: "已确认" },
            { value: "assigned", label: "已派单" },
            { value: "escalated", label: "已升级" },
            { value: "closed", label: "已闭环" },
            { value: "handover", label: "交接中" }
          ]),
          field("assignee", "处理人"),
          select("priority", "优先级", [
            { value: "normal", label: "普通" },
            { value: "high", label: "高" },
            { value: "critical", label: "紧急" }
          ]),
          field("escalationLevel", "升级级别", 'type="number" min="0" max="10" value="0"'),
          field("handoverTo", "交接人"),
          textarea("note", "处理备注")
        ].join(""),
      body: (values) => ({
        status: values.status,
        assignee: values.assignee,
        priority: values.priority,
        escalationLevel: Number(values.escalationLevel || 0),
        handoverTo: values.handoverTo,
        note: values.note
      })
    },
    space: {
      title: "空间能力",
      permission: "space.manage",
      createTitle: "新增空间拓扑",
      listUrl: "/api/space/topology?limit=1000",
      postUrl: () => "/api/space/topology",
      columns: ["边ID", "起点摄像头", "终点摄像头", "关系", "权重", "创建时间"],
      row: (item) => [item.edgeId, item.fromCameraId, item.toCameraId, item.relationType, item.weight, formatDateTime(item.createdAt)],
      form: () =>
        [
          field("fromCameraId", "起点摄像头ID", 'type="number" min="1" required'),
          field("toCameraId", "终点摄像头ID", 'type="number" min="1" required'),
          select("relationType", "关系类型", [
            { value: "adjacent", label: "相邻" },
            { value: "same_floor", label: "同层" },
            { value: "handoff", label: "接续" }
          ]),
          field("weight", "权重", 'type="number" min="0.0001" step="0.1" value="1"')
        ].join(""),
      body: (values) => ({
        fromCameraId: Number(values.fromCameraId || 0),
        toCameraId: Number(values.toCameraId || 0),
        relationType: values.relationType,
        weight: Number(values.weight || 1)
      })
    },
    report: {
      title: "报表计划",
      permission: "report.manage",
      createTitle: "新增报表计划",
      listUrl: "/api/report/schedule/list?limit=200",
      runListUrl: "/api/report/run/list?limit=100",
      postUrl: () => "/api/report/schedule",
      generateUrl: () => "/api/report/generate",
      columns: ["计划ID", "类型", "Cron", "角色", "渠道", "启用", "更新时间"],
      row: (item) => [item.scheduleId, item.reportType, item.cronExpr, item.roleName, item.deliveryChannel, item.enabled ? "是" : "否", formatDateTime(item.updatedAt)],
      form: () =>
        [
          select("reportType", "报表类型", [
            { value: "daily", label: "日报" },
            { value: "weekly", label: "周报" },
            { value: "monthly", label: "月报" }
          ]),
          field("cronExpr", "Cron 表达式", 'value="0 8 * * *"'),
          field("roleName", "推送角色", 'value="building_admin"'),
          select("deliveryChannel", "推送渠道", [
            { value: "inbox", label: "站内" },
            { value: "email", label: "邮件" },
            { value: "webhook", label: "Webhook" }
          ]),
          select("enabled", "是否启用", [
            { value: "true", label: "启用" },
            { value: "false", label: "停用" }
          ])
        ].join(""),
      body: (values) => ({
        reportType: values.reportType,
        cronExpr: values.cronExpr,
        roleName: values.roleName,
        deliveryChannel: values.deliveryChannel,
        enabled: values.enabled !== "false"
      }),
      generateForm: () =>
        [
          select("reportType", "报表类型", [
            { value: "daily", label: "日报" },
            { value: "weekly", label: "周报" },
            { value: "monthly", label: "月报" }
          ]),
          field("roleName", "投递角色", 'value="building_admin"'),
          select("deliveryChannel", "投递渠道", [
            { value: "system", label: "站内" },
            { value: "email", label: "邮件" },
            { value: "webhook", label: "Webhook" }
          ]),
          field("rangeStart", "开始日期", 'type="date"'),
          field("rangeEnd", "结束日期", 'type="date"')
        ].join(""),
      generateBody: (values) => ({
        scheduleId: null,
        reportType: values.reportType,
        rangeStart: values.rangeStart || null,
        rangeEnd: values.rangeEnd || null,
        roleName: values.roleName,
        deliveryChannel: values.deliveryChannel
      }),
      runColumns: ["运行ID", "类型", "开始", "结束", "状态", "生成时间"],
      runRow: (item) => [item.runId, item.reportType, formatDateTime(item.rangeStart), formatDateTime(item.rangeEnd), item.status, formatDateTime(item.generatedAt)]
    },
    tenant: {
      title: "多租户",
      permission: "tenant.manage",
      createTitle: "新增租户项目",
      listUrl: "/api/tenant/list?limit=200",
      scopeListUrl: "/api/tenant/scope/list?limit=200",
      postUrl: () => "/api/tenant/project",
      scopePostUrl: () => "/api/tenant/scope",
      columns: ["租户ID", "租户编码", "租户名称", "启用", "创建时间"],
      row: (item) => [item.tenantId, item.tenantCode, item.tenantName, item.enabled ? "是" : "否", formatDateTime(item.createdAt)],
      form: () =>
        [
          field("tenantCode", "租户编码", 'required'),
          field("tenantName", "租户名称", 'required'),
          select("enabled", "是否启用", [
            { value: "true", label: "启用" },
            { value: "false", label: "停用" }
          ]),
          textarea("configJson", "项目配置 JSON", 'placeholder="{&quot;campus&quot;:&quot;A&quot;}"')
        ].join(""),
      body: (values) => ({
        tenantCode: values.tenantCode,
        tenantName: values.tenantName,
        enabled: values.enabled !== "false",
        configJson: values.configJson || "{}"
      }),
      scopeColumns: ["范围ID", "租户", "角色", "权限 JSON", "创建时间"],
      scopeRow: (item) => [item.scopeId, `${item.tenantCode} / ${item.tenantName}`, item.roleName, item.permissionJson, formatDateTime(item.createdAt)],
      scopeForm: () =>
        [
          field("tenantId", "租户ID", 'type="number" min="1" required'),
          field("roleName", "角色名", 'value="building_admin"'),
          textarea("permissionJson", "权限 JSON", 'placeholder="[&quot;alert.manage&quot;,&quot;space.manage&quot;]"')
        ].join(""),
      scopeBody: (values) => ({
        tenantId: Number(values.tenantId || 0),
        roleName: values.roleName,
        permissionJson: values.permissionJson || "[]"
      })
    },
    ai: {
      title: "AI 平台",
      permission: "ai.platform",
      createTitle: "新增模型供应商",
      listUrl: "/api/ai-platform/providers?limit=200",
      experimentListUrl: "/api/ai-platform/experiments?limit=200",
      postUrl: () => "/api/ai-platform/providers",
      experimentPostUrl: () => "/api/ai-platform/experiments",
      columns: ["供应商ID", "名称", "类型", "模型", "版本", "权重", "启用", "创建时间"],
      row: (item) => [item.providerId, item.providerName, item.providerType, item.modelName, item.modelVersion, item.trafficWeight, item.enabled ? "是" : "否", formatDateTime(item.createdAt)],
      form: () =>
        [
          field("providerName", "供应商名称", 'required'),
          select("providerType", "供应商类型", [
            { value: "internal", label: "内置" },
            { value: "external", label: "外部" },
            { value: "feature_service", label: "特征服务" }
          ]),
          field("endpointUrl", "服务地址", 'placeholder="http://ai-service.local"'),
          field("modelName", "模型名称", 'required'),
          field("modelVersion", "模型版本", 'value="v1"'),
          field("trafficWeight", "流量权重", 'type="number" min="0" max="1000" value="100"'),
          select("enabled", "是否启用", [
            { value: "true", label: "启用" },
            { value: "false", label: "停用" }
          ])
        ].join(""),
      body: (values) => ({
        providerName: values.providerName,
        providerType: values.providerType,
        endpointUrl: values.endpointUrl,
        modelName: values.modelName,
        modelVersion: values.modelVersion,
        trafficWeight: Number(values.trafficWeight || 0),
        enabled: values.enabled !== "false"
      }),
      experimentColumns: ["实验ID", "实验名称", "供应商A", "供应商B", "流量切分", "指标", "启用", "创建时间"],
      experimentRow: (item) => [item.experimentId, item.experimentName, item.providerAId, item.providerBId, item.trafficSplit, item.metricName, item.enabled ? "是" : "否", formatDateTime(item.createdAt)],
      experimentForm: () =>
        [
          field("experimentName", "实验名称", 'required'),
          field("providerAId", "供应商A ID", 'type="number" min="1" required'),
          field("providerBId", "供应商B ID", 'type="number" min="1" required'),
          field("trafficSplit", "A 侧流量百分比", 'type="number" min="0" max="100" value="50"'),
          field("metricName", "质量指标", 'value="precision@10"'),
          select("enabled", "是否启用", [
            { value: "true", label: "启用" },
            { value: "false", label: "停用" }
          ])
        ].join(""),
      experimentBody: (values) => ({
        experimentName: values.experimentName,
        providerAId: Number(values.providerAId || 0),
        providerBId: Number(values.providerBId || 0),
        trafficSplit: Number(values.trafficSplit || 50),
        metricName: values.metricName,
        enabled: values.enabled !== "false"
      })
    }
  };

  const permissionAliases = Object.freeze({
    alert: "alert.manage",
    ai: "ai.settings",
    ai_settings: "ai.settings",
    report: "report.manage",
    reports: "report.manage",
    space: "space.manage",
    tenant: "tenant.manage",
    tenants: "tenant.manage",
    ai_platform: "ai.platform"
  });

  const normalizePermission = (value) => {
    const key = String(value || "").trim().toLowerCase();
    return permissionAliases[key] || key;
  };

  const loadCurrentSession = async () => {
    try {
      const result = await requestJson(`${apiBase}/api/auth/me`);
      if (!result.ok || result.data?.code !== 0) return null;
      return result.data?.data || null;
    } catch {
      return null;
    }
  };

  const hasPermission = (session, permission) => {
    const role = String(session?.role || "").trim().toLowerCase();
    if (role === "super_admin") return true;
    const permissions = new Set((Array.isArray(session?.permissions) ? session.permissions : []).map(normalizePermission));
    return permissions.has("all") || permissions.has(normalizePermission(permission));
  };

  const getAllowedSectionKeys = (session) => {
    return Object.keys(sections).filter((key) => hasPermission(session, sections[key].permission));
  };

  function readForm() {
    if (window.aura?.readForm) return window.aura.readForm(formEl);
    const values = {};
    formEl.querySelectorAll("input[name], select[name], textarea[name]").forEach((fieldEl) => {
      values[fieldEl.name] = String(fieldEl.value ?? "").trim();
    });
    return values;
  }

  function renderRows(rows) {
    const section = sections[activeSection];
    window.aura?.renderTable?.({
      wrap: tableWrap,
      head: tableHead,
      body: tableBody,
      columns: section.columns,
      rows,
      emptyColspan: section.columns.length,
      emptyText: `${section.title}暂无数据`,
      rowHtml: (item) => `<tr>${section.row(item).map((value) => `<td>${escapeHtml(value ?? "—")}</td>`).join("")}</tr>`
    });
  }

  function clearTable() {
    if (window.aura?.clearTable) {
      window.aura.clearTable({ wrap: tableWrap, head: tableHead, body: tableBody });
      return;
    }
    if (tableWrap) tableWrap.hidden = true;
    if (tableHead) tableHead.innerHTML = "";
    if (tableBody) tableBody.innerHTML = "";
  }

  function syncSectionAccess() {
    const allowed = new Set(allowedSections);
    document.querySelectorAll(".extensions-tab").forEach((btn) => {
      const key = btn.getAttribute("data-section") || "";
      const visible = allowed.has(key);
      btn.hidden = !visible;
      btn.disabled = !visible;
    });

    if (allowedSections.length > 0) return true;
    clearTable();
    openCreateBtn.hidden = true;
    refreshBtn.hidden = true;
    if (generateReportBtn) generateReportBtn.hidden = true;
    if (openAiExperimentBtn) openAiExperimentBtn.hidden = true;
    if (openTenantScopeBtn) openTenantScopeBtn.hidden = true;
    status?.set("当前账号没有可用的产品化扩展管理权限，请联系超级管理员授权。", { isError: true });
    return false;
  }

  async function loadSection() {
    const section = sections[activeSection];
    status?.set("加载中...");
    const result = await requestJson(`${apiBase}${section.listUrl}`);
    const data = result.data;
    if (!result.ok || data?.code !== 0) {
      renderRows([]);
      status?.set(data?.msg || "加载失败", { isError: true });
      return;
    }
    renderRows(Array.isArray(data?.data) ? data.data : []);
    if (activeSection === "report") {
      await loadReportRuns();
    }
    if (activeSection === "tenant") {
      await loadTenantScopes();
    }
    if (activeSection === "ai") {
      await loadAiExperiments();
    }
    status?.set(`${section.title}已刷新`);
  }

  async function appendInlineTable(title, columns, rows, rowBuilder) {
    const head = columns.map((label) => `<th>${escapeHtml(label)}</th>`).join("");
    const body = rows.length
      ? rows.map((item) => `<tr>${rowBuilder(item).map((value) => `<td>${escapeHtml(value ?? "—")}</td>`).join("")}</tr>`).join("")
      : `<tr><td colspan="${columns.length}">${escapeHtml(title)}暂无数据</td></tr>`;
    tableBody.insertAdjacentHTML(
      "beforeend",
      `<tr class="extensions-subhead"><th colspan="${sections[activeSection].columns.length}">${escapeHtml(title)}</th></tr><tr class="extensions-inline-head">${head}</tr>${body}`
    );
  }

  async function loadReportRuns() {
    const section = sections.report;
    const result = await requestJson(`${apiBase}${section.runListUrl}`);
    const data = result.data;
    const rows = result.ok && data?.code === 0 && Array.isArray(data?.data) ? data.data : [];
    await appendInlineTable("生成记录", section.runColumns, rows, (item) => section.runRow(item));
  }

  async function loadTenantScopes() {
    const section = sections.tenant;
    const result = await requestJson(`${apiBase}${section.scopeListUrl}`);
    const data = result.data;
    const rows = result.ok && data?.code === 0 && Array.isArray(data?.data) ? data.data : [];
    await appendInlineTable("权限范围", section.scopeColumns, rows, (item) => section.scopeRow(item));
  }

  async function loadAiExperiments() {
    const section = sections.ai;
    const result = await requestJson(`${apiBase}${section.experimentListUrl}`);
    const data = result.data;
    const rows = result.ok && data?.code === 0 && Array.isArray(data?.data) ? data.data : [];
    const expHead = section.experimentColumns.map((label) => `<th>${escapeHtml(label)}</th>`).join("");
    const expRows = rows.length
      ? rows
          .map((item) => `<tr>${section.experimentRow(item).map((value) => `<td>${escapeHtml(value ?? "—")}</td>`).join("")}</tr>`)
          .join("")
      : `<tr><td colspan="${section.experimentColumns.length}">AI 实验暂无数据</td></tr>`;
    tableBody.insertAdjacentHTML(
      "beforeend",
      `<tr class="extensions-subhead"><th colspan="${section.columns.length}">A/B 实验</th></tr><tr class="extensions-inline-head">${expHead}</tr>${expRows}`
    );
  }

  function setActiveSection(next) {
    if (!allowedSections.length && !syncSectionAccess()) return;
    const fallbackSection = allowedSections[0] || "workflow";
    activeSection = sections[next] && allowedSections.includes(next) ? next : fallbackSection;
    document.querySelectorAll(".extensions-tab").forEach((btn) => {
      btn.classList.toggle("is-active", btn.getAttribute("data-section") === activeSection);
    });
    openCreateBtn.textContent = activeSection === "workflow" ? "更新" : "新增";
    if (generateReportBtn) generateReportBtn.hidden = activeSection !== "report";
    if (openAiExperimentBtn) openAiExperimentBtn.hidden = activeSection !== "ai";
    if (openTenantScopeBtn) openTenantScopeBtn.hidden = activeSection !== "tenant";
    loadSection().catch((error) => status?.set(`加载失败：${error.message}`, { isError: true }));
  }

  function openCreateModal() {
    mode = activeSection;
    const section = sections[mode];
    modalTitle.textContent = section.createTitle;
    formEl.innerHTML = section.form();
    window.aura?.openModal?.(modal, { focus: "input, select, textarea" });
  }

  function openAiExperimentModal() {
    mode = "aiExperiment";
    modalTitle.textContent = "新增 A/B 实验";
    formEl.innerHTML = sections.ai.experimentForm();
    window.aura?.openModal?.(modal, { focus: "input, select, textarea" });
  }

  function openReportGenerateModal() {
    mode = "reportGenerate";
    modalTitle.textContent = "生成报表";
    formEl.innerHTML = sections.report.generateForm();
    window.aura?.openModal?.(modal, { focus: "input, select, textarea" });
  }

  function openTenantScopeModal() {
    mode = "tenantScope";
    modalTitle.textContent = "配置租户权限范围";
    formEl.innerHTML = sections.tenant.scopeForm();
    window.aura?.openModal?.(modal, { focus: "input, select, textarea" });
  }

  async function saveCurrent() {
    const isAiExperiment = mode === "aiExperiment";
    const isReportGenerate = mode === "reportGenerate";
    const isTenantScope = mode === "tenantScope";
    const section = isAiExperiment ? sections.ai : isReportGenerate ? sections.report : isTenantScope ? sections.tenant : sections[mode];
    const values = readForm();
    const url = isAiExperiment
      ? section.experimentPostUrl(values)
      : isReportGenerate
        ? section.generateUrl(values)
        : isTenantScope
          ? section.scopePostUrl(values)
          : section.postUrl(values);
    const body = isAiExperiment
      ? section.experimentBody(values)
      : isReportGenerate
        ? section.generateBody(values)
        : isTenantScope
          ? section.scopeBody(values)
          : section.body(values);
    window.aura?.setBusy?.(saveBtn, true);
    try {
      const result = await requestJson(`${apiBase}${url}`, { method: "POST", body });
      const data = result.data;
      if (!result.ok || data?.code !== 0) {
        status?.set(data?.msg || "保存失败", { isError: true });
        return;
      }
      window.aura?.closeModal?.(modal);
      status?.set(data?.msg || "保存成功");
      await loadSection();
    } catch (error) {
      status?.set(`保存失败：${error.message}`, { isError: true });
    } finally {
      window.aura?.setBusy?.(saveBtn, false);
    }
  }

  document.querySelectorAll(".extensions-tab").forEach((btn) => {
    btn.addEventListener("click", () => setActiveSection(btn.getAttribute("data-section") || "workflow"));
  });
  refreshBtn?.addEventListener("click", () => loadSection().catch((error) => status?.set(`加载失败：${error.message}`, { isError: true })));
  openCreateBtn?.addEventListener("click", openCreateModal);
  openAiExperimentBtn?.addEventListener("click", openAiExperimentModal);
  generateReportBtn?.addEventListener("click", openReportGenerateModal);
  openTenantScopeBtn?.addEventListener("click", openTenantScopeModal);
  saveBtn?.addEventListener("click", () => saveCurrent());
  window.aura?.bindModalDismiss?.(modal);

  async function bootstrap() {
    const session = await loadCurrentSession();
    allowedSections = getAllowedSectionKeys(session);
    if (!syncSectionAccess()) return;
    setActiveSection(allowedSections[0]);
  }

  bootstrap().catch((error) => status?.set(`加载失败：${error.message}`, { isError: true }));
})();

