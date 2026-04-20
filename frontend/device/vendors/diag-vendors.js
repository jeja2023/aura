/* 文件：设备诊断厂商注册表（diag-vendors.js） | File: Device Diagnostic Vendor Registry */
(function () {
  const VENDORS = Object.freeze([
    {
      key: "hik-isapi",
      label: "海康威视 · ISAPI",
      enabled: true,
      unavailableHint: ""
    },
    {
      key: "dahua-isapi",
      label: "大华 · ISAPI（规划中）",
      enabled: false,
      unavailableHint: "当前仅开放“海康威视 · ISAPI”诊断能力，其他品牌接入功能正在规划中。"
    },
    {
      key: "onvif-common",
      label: "通用 · ONVIF（规划中）",
      enabled: false,
      unavailableHint: "当前仅开放“海康威视 · ISAPI”诊断能力，其他品牌接入功能正在规划中。"
    }
  ]);

  function getDefaultVendorKey() {
    const firstEnabled = VENDORS.find((item) => item.enabled);
    return firstEnabled ? firstEnabled.key : "hik-isapi";
  }

  function getVendorByKey(key) {
    const target = String(key || "").trim().toLowerCase();
    return VENDORS.find((item) => item.key === target) || null;
  }

  function normalizeVendorKey(key) {
    const hit = getVendorByKey(key);
    return hit ? hit.key : getDefaultVendorKey();
  }

  function isVendorEnabled(key) {
    const hit = getVendorByKey(key);
    return Boolean(hit && hit.enabled);
  }

  function getUnavailableHint(key) {
    const hit = getVendorByKey(key);
    if (!hit || hit.enabled) return "";
    return String(hit.unavailableHint || "").trim();
  }

  function renderOptions(selectEl) {
    if (!(selectEl instanceof HTMLSelectElement)) return;
    const previous = String(selectEl.value || "").trim().toLowerCase();
    selectEl.innerHTML = VENDORS.map(
      (item) => `<option value="${item.key}" ${item.enabled ? "" : "disabled"}>${item.label}</option>`
    ).join("");
    selectEl.value = normalizeVendorKey(previous);
  }

  window.auraDeviceDiagVendors = {
    list: VENDORS,
    getDefaultVendorKey,
    normalizeVendorKey,
    isVendorEnabled,
    getUnavailableHint,
    renderOptions
  };
})();
