/* 文件：报表导出页脚本（export.js） | File: Export Page Script */
(function () {
  function setResult(message, isError = false) {
    const el = document.getElementById("result");
    if (!el) return;
    const text = String(message || "").trim();
    el.hidden = !text;
    el.textContent = text;
    el.classList.toggle("is-error", Boolean(text) && isError);
  }

  function getValue(id) {
    const el = document.getElementById(id);
    if (!el) return "";
    return String(el.value || "").trim();
  }

  async function handleExport() {
    setResult("");
    const dataset = getValue("dataset").toLowerCase();
    const keyword = getValue("keyword");

    if (!window.aura || typeof window.aura.exportDataset !== "function") {
      setResult("导出能力未加载：请检查 common/shell.js 是否正常引入。", true);
      return;
    }

    const ok = await window.aura.exportDataset({
      dataset,
      keyword,
      onError: (msg) => {
        setResult(msg || "导出失败", true);
      },
      onSuccess: () => {
        setResult("导出请求已提交，正在打开下载链接...");
      }
    });

    if (!ok) {
      // 用户取消格式选择时，不提示错误
      return;
    }
  }

  function clearForm() {
    const keyword = document.getElementById("keyword");
    if (keyword) keyword.value = "";
    setResult("");
  }

  function bootstrap() {
    const exportBtn = document.getElementById("exportBtn");
    const clearBtn = document.getElementById("clearBtn");
    exportBtn?.addEventListener("click", handleExport);
    clearBtn?.addEventListener("click", clearForm);

    document.addEventListener("keydown", (e) => {
      if (e.key === "Enter") {
        const active = document.activeElement;
        if (active && (active.id === "keyword" || active.id === "dataset")) {
          e.preventDefault();
          void handleExport();
        }
      }
    });
  }

  bootstrap();
})();
