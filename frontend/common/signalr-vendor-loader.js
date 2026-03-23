/* 文件：SignalR 库加载器（signalr-vendor-loader.js） | File: SignalR vendor script loader — 仅本地 vendor，不使用 CDN */
(function () {
  "use strict";

  function loadScript(src) {
    return new Promise(function (resolve, reject) {
      var s = document.createElement("script");
      s.src = src;
      s.async = false;
      s.onload = function () {
        resolve();
      };
      s.onerror = function () {
        reject(new Error("加载失败：" + src));
      };
      document.head.appendChild(s);
    });
  }

  function localVendor(fileName) {
    return new URL("../common/vendor/" + fileName, document.baseURI).href;
  }

  var el = document.getElementById("auraSignalrVendorLoader") || document.currentScript;
  var pageRel = (el && el.getAttribute("data-aura-page-js")) || "./index.js";
  var pageScriptUrl = new URL(pageRel, document.baseURI).href;

  loadScript(localVendor("signalr.min.js"))
    .then(function () {
      return loadScript(pageScriptUrl);
    })
    .catch(function (err) {
      var list = document.getElementById("events");
      if (list) {
        var li = document.createElement("li");
        li.textContent =
          "SignalR 脚本加载失败：" +
          (err && err.message ? err.message : String(err)) +
          "。请确认 frontend/common/vendor/signalr.min.js 存在。";
        list.appendChild(li);
      }
    });
})();
