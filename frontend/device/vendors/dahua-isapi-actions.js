/* 文件：大华诊断动作模块（dahua-isapi-actions.js） | File: Dahua Diagnostic Actions Module */
(function () {
  function noop() {}

  function setDisplayFallback(text) {
    const el = document.getElementById("hikIsapiResult");
    if (!el) return;
    const msg = String(text || "").trim() || "大华 ISAPI 诊断能力正在接入中。";
    el.textContent = msg;
    el.hidden = false;
  }

  function init() {
    // 预留：后续接入大华诊断按钮绑定与接口调用
  }

  function populateDeviceSelect(_rows) {
    // 预留：沿用当前通用设备选择器，无需额外处理
  }

  function setDeviceIdInForm(deviceId) {
    const id = Number(deviceId);
    if (!Number.isFinite(id) || id <= 0) return;
    const manualEl = document.getElementById("hikDeviceIdManual");
    if (manualEl) manualEl.value = String(id);
    const selectEl = document.getElementById("hikDeviceSelect");
    if (selectEl) selectEl.value = String(id);
  }

  window.auraDahuaIsapiActions = {
    init,
    populateDeviceSelect,
    setDeviceIdInForm,
    setDisplay: setDisplayFallback,
    startSignalR: noop
  };
})();
