/* 文件：ECharts 库加载器（echarts-vendor-loader.js） | File: ECharts vendor script loader — 仅本地 vendor，不使用 CDN */
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

  var el = document.getElementById("auraEchartsVendorLoader") || document.currentScript;
  var pageRel = (el && el.getAttribute("data-aura-page-js")) || "./stats.js";
  var statsJsUrl = new URL(pageRel, document.baseURI).href;

  loadScript(localVendor("echarts.min.js"))
    .then(function () {
      return loadScript(statsJsUrl);
    })
    .catch(function (err) {
      var msg =
        "ECharts 加载失败：" +
        (err && err.message ? err.message : String(err)) +
        "。请确认 frontend/common/vendor/echarts.min.js 存在。";
      var pre = document.getElementById("statsStatus") || document.getElementById("result");
      if (pre) {
        pre.textContent = msg;
        pre.hidden = false;
        if (pre.classList) pre.classList.add("is-error");
      } else {
        var root = document.querySelector(".page") || document.body;
        var extra = document.createElement("pre");
        extra.textContent = msg;
        root.appendChild(extra);
      }
    });
})();
