/* 文件：三维态势页第三方库加载器（scene-vendor-loader.js） | File: Scene vendor script loader — 仅本地 vendor，不使用 CDN */
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

  function showFail(message) {
    var pre = document.getElementById("result");
    if (pre) {
      pre.textContent = message;
    }
  }

  function localVendor(fileName) {
    return new URL("../common/vendor/" + fileName, document.baseURI).href;
  }

  var el = document.getElementById("auraSceneVendorLoader") || document.currentScript;
  var pageRel = (el && el.getAttribute("data-aura-page-js")) || "./scene.js";
  var sceneJsUrl = new URL(pageRel, document.baseURI).href;

  loadScript(localVendor("three.min.js"))
    .then(function () {
      if (typeof THREE === "undefined") {
        return Promise.reject(new Error("three.min.js 已加载但未定义全局 THREE，请检查文件是否完整"));
      }
      return loadScript(localVendor("signalr.min.js"));
    })
    .then(function () {
      return loadScript(sceneJsUrl);
    })
    .catch(function (err) {
      showFail(
        "三维态势依赖加载失败：" +
          (err && err.message ? err.message : String(err)) +
          "。请确认 frontend/common/vendor/ 下存在 three.min.js、signalr.min.js（见该目录 README.md）。"
      );
    });
})();
