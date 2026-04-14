/* 文件：应用外壳脚本（shell.js） | File: App Shell Script */
(function () {
  /** 与《开发计划》模块对应的导航分组 */
  const NAV_GROUPS = [
    { title: "总览", items: [{ href: "/index/", label: "态势看板" }] },
    {
      title: "空间与地图",
      items: [
        { href: "/campus/", label: "集宿资源树" },
        { href: "/floor/", label: "楼层图纸" },
        { href: "/camera/", label: "摄像头布点" },
        { href: "/roi/", label: "ROI 防区" }
      ]
    },
    {
      title: "接入与抓拍",
      items: [
        { href: "/device/", label: "NVR 设备" },
        { href: "/capture/", label: "抓拍记录" }
      ]
    },
    { title: "态势与可视化", items: [{ href: "/scene/", label: "三维空间态势" }] },
    {
      title: "研判与追踪",
      items: [
        { href: "/alert/", label: "告警中心" },
        { href: "/judge/", label: "归寝研判" },
        { href: "/track/", label: "轨迹回放" },
        { href: "/search/", label: "以图搜轨" }
      ]
    },
    {
      title: "统计与导出",
      items: [
        { href: "/stats/", label: "统计驾驶舱" },
        { href: "/export/", label: "报表导出" }
      ]
    },
    {
      title: "权限与组织",
      items: [
        { href: "/role/", label: "角色管理" },
        { href: "/user/", label: "用户管理" }
      ]
    },
    { title: "审计与运维", items: [{ href: "/log/", label: "操作日志" }] }
  ];
  const SUPER_ADMIN_ONLY_PATHS = new Set(["/role/", "/user/", "/log/"]);
  const PAGE_SESSION_ID = `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`;
  const PAGE_ENTER_MS = Date.now();
  let pageLeaveReported = false;
  let toastHostEl = null;

  function normPath(p) {
    const s = (p || "/").replace(/\/+$/, "") || "/";
    return s;
  }

  function currentPath() {
    return normPath(window.location.pathname);
  }

  function renderSidebar(container, role) {
    const cur = currentPath();
    const isSuperAdmin = role === "super_admin";
    let html =
      '<div class="app-brand" title="寓瞳 · 智能集宿区视觉解析平台">寓瞳</div><nav class="app-nav" aria-label="业务模块导航">';
    for (const g of NAV_GROUPS) {
      const visibleItems = g.items.filter((it) => isSuperAdmin || !SUPER_ADMIN_ONLY_PATHS.has(it.href));
      if (visibleItems.length === 0) continue;
      html += `<section class="nav-group"><div class="nav-group-title">${g.title}</div><ul class="nav-group-items">`;
      for (const it of visibleItems) {
        const active = normPath(it.href) === cur;
        const cls = active ? "nav-link is-active" : "nav-link";
        const ac = active ? ' aria-current="page"' : "";
        html += `<li><a class="${cls}" href="${it.href}"${ac}>${it.label}</a></li>`;
      }
      html += "</ul></section>";
    }
    html += "</nav>";
    container.innerHTML = html;
  }

  function setPageTitle() {
    const el = document.querySelector(".app-page-title");
    if (!el) return;
    const t = document.body.getAttribute("data-shell-title");
    el.textContent = t || document.title || "寓瞳";
  }

  function mountThemeControl() {
    const slot = document.querySelector(".app-topbar-actions");
    if (!slot || document.getElementById("auraThemeToggleBtn")) return;
    const wrap = document.createElement("div");
    wrap.className = "aura-theme-field";
    const pref = window.auraThemePref;

    const btn = document.createElement("button");
    btn.type = "button";
    btn.id = "auraThemeToggleBtn";
    btn.className = "aura-theme-icon-button";
    btn.setAttribute("aria-label", "切换主题");
    btn.setAttribute("title", "切换主题");

    function getMode() {
      if (pref && typeof pref.get === "function") return pref.get();
      return "light";
    }

    function renderIcon(mode) {
      // mode 表示当前主题；使用“当前主题图标”便于用户感知当前状态
      if (mode === "dark") {
        // 月亮
        btn.innerHTML =
          '<svg viewBox="0 0 24 24" aria-hidden="true" focusable="false"><path d="M21 13.2A7.8 7.8 0 0 1 10.8 3a7.1 7.1 0 1 0 10.2 10.2Z" fill="currentColor"/></svg>';
      } else {
        // 太阳
        btn.innerHTML =
          '<svg viewBox="0 0 24 24" aria-hidden="true" focusable="false"><circle cx="12" cy="12" r="4.5" fill="currentColor"/><g stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M12 2v2.5"/><path d="M12 19.5V22"/><path d="M2 12h2.5"/><path d="M19.5 12H22"/><path d="M4.2 4.2l1.8 1.8"/><path d="M18 18l1.8 1.8"/><path d="M19.8 4.2L18 6"/><path d="M6 18l-1.8 1.8"/></g></svg>';
      }
    }

    btn.addEventListener("click", () => {
      const cur = getMode();
      const next = cur === "dark" ? "light" : "dark";
      if (pref && typeof pref.set === "function") pref.set(next);
      renderIcon(next);
    });

    wrap.appendChild(btn);
    slot.insertBefore(wrap, slot.firstChild);
    renderIcon(getMode());
  }

  function mountLogout() {
    const slot = document.querySelector(".app-topbar-actions");
    if (!slot || document.getElementById("auraLogoutBtn")) return;
    const btn = document.createElement("button");
    btn.type = "button";
    btn.id = "auraLogoutBtn";
    btn.className = "btn-primary";
    btn.textContent = "退出登录";
    btn.addEventListener("click", async () => {
      localStorage.removeItem("token");
      try {
        await fetch("/api/auth/logout", { method: "POST" });
      } catch {
        // 网络异常不阻断前端登出跳转
      }
      window.location.href = "/login/";
    });
    slot.appendChild(btn);
  }

  function mountSidebarToggle() {
    const aside = document.getElementById("auraSidebar");
    const btn = document.getElementById("auraSidebarToggle");
    if (!btn || !aside) return;
    btn.addEventListener("click", () => {
      aside.classList.toggle("is-open");
      document.body.classList.toggle("app-sidebar-open");
    });
    aside.addEventListener("click", (e) => {
      if (e.target.closest("a") && window.innerWidth < 900) {
        aside.classList.remove("is-open");
        document.body.classList.remove("app-sidebar-open");
      }
    });
  }

  async function loadCurrentRole() {
    try {
      const res = await fetch("/api/auth/me", { credentials: "include" });
      if (!res.ok) return "";
      const data = await res.json();
      return data?.data?.role || "";
    } catch {
      return "";
    }
  }

  async function reportPageView(eventType, stayMs) {
    try {
      await fetch("/api/audit/page-view", {
        method: "POST",
        credentials: "include",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          pagePath: window.location.pathname,
          pageTitle: document.body?.getAttribute("data-shell-title") || document.title || "",
          eventType,
          stayMs,
          sessionId: PAGE_SESSION_ID
        })
      });
    } catch {
      // 页面访问审计失败不影响页面使用
    }
  }

  function reportPageLeave() {
    if (pageLeaveReported) return;
    pageLeaveReported = true;
    const stayMs = Math.max(0, Date.now() - PAGE_ENTER_MS);
    const payload = JSON.stringify({
      pagePath: window.location.pathname,
      pageTitle: document.body?.getAttribute("data-shell-title") || document.title || "",
      eventType: "leave",
      stayMs,
      sessionId: PAGE_SESSION_ID
    });
    try {
      if (navigator.sendBeacon) {
        const ok = navigator.sendBeacon("/api/audit/page-view", new Blob([payload], { type: "application/json" }));
        if (ok) return;
      }
    } catch {
      // ignore and fallback
    }
    fetch("/api/audit/page-view", {
      method: "POST",
      credentials: "include",
      keepalive: true,
      headers: { "Content-Type": "application/json" },
      body: payload
    }).catch(() => {});
  }

  function ensureToastHost() {
    if (toastHostEl && document.body.contains(toastHostEl)) return toastHostEl;
    toastHostEl = document.getElementById("auraToastHost");
    if (toastHostEl) return toastHostEl;
    toastHostEl = document.createElement("div");
    toastHostEl.id = "auraToastHost";
    toastHostEl.className = "aura-toast-host";
    document.body.appendChild(toastHostEl);
    return toastHostEl;
  }

  function showToast(message, isError = false, durationMs = 2200) {
    const text = String(message || "").trim();
    if (!text) return;
    const host = ensureToastHost();
    const item = document.createElement("div");
    item.className = isError ? "aura-toast is-error" : "aura-toast";
    item.textContent = text;
    host.appendChild(item);
    requestAnimationFrame(() => item.classList.add("is-show"));
    window.setTimeout(() => {
      item.classList.remove("is-show");
      window.setTimeout(() => item.remove(), 220);
    }, Math.max(800, durationMs));
  }

  function bridgeStatusToToast() {
    const statusEls = Array.from(document.querySelectorAll(".aura-status"));
    if (statusEls.length === 0) return;

    const handle = (el) => {
      if (!el || el.hidden) return;
      const msg = (el.textContent || "").trim();
      if (!msg) return;
      const isError = el.classList.contains("is-error");
      const lastMsg = el.dataset.toastLastMsg || "";
      if (lastMsg === msg) return;
      el.dataset.toastLastMsg = msg;
      if (isError) return;
      showToast(msg, false);
      el.hidden = true;
    };

    statusEls.forEach((el) => {
      handle(el);
      const ob = new MutationObserver(() => handle(el));
      ob.observe(el, { attributes: true, attributeFilter: ["hidden", "class"], childList: true, subtree: true, characterData: true });
    });
  }

  async function bootstrap() {
    const sidebar = document.getElementById("auraSidebar");
    if (sidebar) {
      const role = await loadCurrentRole();
      renderSidebar(sidebar, role);
    }
    setPageTitle();
    mountThemeControl();
    mountLogout();
    mountSidebarToggle();
    bridgeStatusToToast();
    reportPageView("enter", 0);
    window.addEventListener("pagehide", reportPageLeave);
    initAnimateNumbers();
  }

  /** 数字跳动动效：从 0 渐变到目标值 */
  function initAnimateNumbers() {
    const els = document.querySelectorAll("[data-aura-number]");
    els.forEach((el) => {
      const target = parseFloat(el.getAttribute("data-aura-number")) || 0;
      const duration = 1200;
      const start = 0;
      let startTime = null;

      function step(timestamp) {
        if (!startTime) startTime = timestamp;
        const progress = Math.min((timestamp - startTime) / duration, 1);
        const current = progress * (target - start) + start;
        el.textContent = Math.floor(current).toLocaleString();
        if (progress < 1) {
          window.requestAnimationFrame(step);
        } else {
          el.textContent = target.toLocaleString();
        }
      }
      window.requestAnimationFrame(step);
    });
  }

  // 暴露工具
  /** 全局：时间展示统一为 yyyy-MM-dd HH:mm:ss，不出现 ISO 中的字母 T */
  window.formatDateTimeDisplay = function formatDateTimeDisplay(v, empty) {
    if (v === null || v === undefined || v === "") return empty === undefined ? "—" : empty;
    const d = new Date(v);
    if (!Number.isNaN(d.getTime())) {
      const pad = (n) => String(n).padStart(2, "0");
      return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`;
    }
    const s = String(v);
    const m = s.match(/^(\d{4}-\d{2}-\d{2})T(\d{2}:\d{2}:\d{2})/);
    if (m) return `${m[1]} ${m[2]}`;
    return s.replace("T", " ").replace(/\.\d+/, "").trim();
  };

  window.aura = {
    paginateArray: (rows, page = 1, pageSize = 20) => {
      const list = Array.isArray(rows) ? rows : [];
      const safePageSize = Math.max(1, Number(pageSize) || 20);
      const total = list.length;
      const totalPages = Math.max(1, Math.ceil(total / safePageSize));
      const safePage = Math.min(totalPages, Math.max(1, Number(page) || 1));
      const start = (safePage - 1) * safePageSize;
      return {
        page: safePage,
        pageSize: safePageSize,
        total,
        totalPages,
        rows: list.slice(start, start + safePageSize)
      };
    },
    renderPager: (container, options) => {
      if (!container || !options) return;
      const pageSizeOptions = Array.isArray(options.pageSizeOptions) && options.pageSizeOptions.length > 0 ? options.pageSizeOptions : [10, 20, 50, 100];
      const total = Math.max(0, Number(options.total) || 0);
      const pageSize = Math.max(1, Number(options.pageSize) || pageSizeOptions[0]);
      const totalPages = Math.max(1, Math.ceil(total / pageSize));
      const page = Math.min(totalPages, Math.max(1, Number(options.page) || 1));
      const onChange = typeof options.onChange === "function" ? options.onChange : null;
      container.hidden = total <= 0;
      if (container.hidden) {
        container.innerHTML = "";
        return;
      }
      const disabledPrev = page <= 1 ? "disabled" : "";
      const disabledNext = page >= totalPages ? "disabled" : "";
      container.classList.add("aura-pager");
      container.innerHTML = `<button type="button" class="btn-secondary aura-pager-btn" data-page="1" ${disabledPrev}>首页</button>
<button type="button" class="btn-secondary aura-pager-btn" data-page="${Math.max(1, page - 1)}" ${disabledPrev}>上一页</button>
<span class="aura-pager-info">第 ${page} / ${totalPages} 页（共 ${total} 条）</span>
<button type="button" class="btn-secondary aura-pager-btn" data-page="${Math.min(totalPages, page + 1)}" ${disabledNext}>下一页</button>
<button type="button" class="btn-secondary aura-pager-btn" data-page="${totalPages}" ${disabledNext}>末页</button>
<label class="aura-pager-size-label">每页
  <select class="aura-pager-size">
    ${pageSizeOptions.map((size) => `<option value="${size}" ${Number(size) === pageSize ? "selected" : ""}>${size}</option>`).join("")}
  </select>
  条
</label>`;
      container.querySelectorAll("[data-page]").forEach((btn) => {
        btn.addEventListener("click", () => {
          if (!onChange) return;
          const nextPage = Number(btn.getAttribute("data-page") || page);
          onChange(nextPage, pageSize);
        });
      });
      const sizeEl = container.querySelector(".aura-pager-size");
      if (sizeEl) {
        sizeEl.addEventListener("change", () => {
          if (!onChange) return;
          const nextSize = Math.max(1, Number(sizeEl.value) || pageSize);
          onChange(1, nextSize);
        });
      }
    },
    toast: (message, isError = false, durationMs = 2200) => showToast(message, isError, durationMs),
    animateNumber: (el, target, duration = 1000) => {
      let startTime = null;
      function step(timestamp) {
        if (!startTime) startTime = timestamp;
        const progress = Math.min((timestamp - startTime) / duration, 1);
        el.textContent = Math.floor(progress * target).toLocaleString();
        if (progress < 1) window.requestAnimationFrame(step);
        else el.textContent = target.toLocaleString();
      }
      window.requestAnimationFrame(step);
    }
  };

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", bootstrap);
  } else {
    bootstrap();
  }
})();
