/* 文件：主题偏好（theme-pref.js） | File: Theme preference bootstrap */
(function () {
  var storageKey = "aura_color_mode";

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

  applyToDocument(readStoredMode());

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
