/* 文件：主题偏好（theme-pref.js） | File: Theme preference bootstrap */
(function () {
  var storageKey = "aura_color_mode";
  var faviconHref = "/common/favicon.svg";

  function normalize(mode) {
    return mode === "dark" ? "dark" : "light";
  }

  function applyToDocument(mode) {
    var m = normalize(mode);
    document.documentElement.setAttribute("data-aura-theme", m);
  }

  function readStoredMode() {
    try {
      var raw = localStorage.getItem(storageKey);
      if (raw === "dark" || raw === "light") return raw;
      return "light";
    } catch {
      return "light";
    }
  }

  function persist(mode) {
    try {
      localStorage.setItem(storageKey, normalize(mode));
    } catch {
      /* 忽略存储不可用 */
    }
  }

  function ensureFavicon() {
    var head = document.head || document.getElementsByTagName("head")[0];
    if (!head) return;
    var selectors = ['link[rel="icon"]', 'link[rel="shortcut icon"]'];
    for (var i = 0; i < selectors.length; i += 1) {
      var existing = head.querySelector(selectors[i]);
      if (existing) {
        existing.setAttribute("href", faviconHref);
        existing.setAttribute("type", "image/svg+xml");
        return;
      }
    }
    var link = document.createElement("link");
    link.setAttribute("rel", "icon");
    link.setAttribute("type", "image/svg+xml");
    link.setAttribute("href", faviconHref);
    head.appendChild(link);
  }

  applyToDocument(readStoredMode());
  ensureFavicon();

  window.auraThemePref = {
    storageKey: storageKey,
    get: readStoredMode,
    set: function (mode) {
      var m = normalize(mode);
      persist(m);
      applyToDocument(m);
    }
  };
})();
