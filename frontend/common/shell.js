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
    { title: "审计与运维", items: [{ href: "/log/", label: "操作与系统日志" }] }
  ];

  function normPath(p) {
    const s = (p || "/").replace(/\/+$/, "") || "/";
    return s;
  }

  function currentPath() {
    return normPath(window.location.pathname);
  }

  function renderSidebar(container) {
    const cur = currentPath();
    let html =
      '<div class="app-brand" title="寓瞳 · 智能集宿区视觉解析平台">寓瞳</div><nav class="app-nav" aria-label="业务模块导航">';
    for (const g of NAV_GROUPS) {
      html += `<section class="nav-group"><div class="nav-group-title">${g.title}</div><ul class="nav-group-items">`;
      for (const it of g.items) {
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

  function bootstrap() {
    const sidebar = document.getElementById("auraSidebar");
    if (sidebar) renderSidebar(sidebar);
    setPageTitle();
    mountThemeControl();
    mountLogout();
    mountSidebarToggle();
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
  window.aura = {
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
