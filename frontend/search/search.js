/* 文件：搜轨页脚本（search.js） | File: Search Script */
const apiBase = "https://localhost:5001";
const resultEl = document.getElementById("result");

function getToken() {
  return localStorage.getItem("token") ?? "";
}

function setResult(data) {
  resultEl.textContent = typeof data === "string" ? data : JSON.stringify(data, null, 2);
}

function fileToBase64(file) {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => {
      const raw = String(reader.result || "");
      const idx = raw.indexOf(",");
      resolve(idx >= 0 ? raw.slice(idx + 1) : raw);
    };
    reader.onerror = () => reject(reader.error);
    reader.readAsDataURL(file);
  });
}

async function runSearch() {
  const file = document.getElementById("file").files?.[0];
  const topK = Number(document.getElementById("topk").value) || 10;
  if (!file) {
    setResult("请先选择图片");
    return;
  }
  setResult("检索中...");
  try {
    const imageBase64 = await fileToBase64(file);
    const extRes = await fetch(`${apiBase}/api/vector/extract`, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        Authorization: `Bearer ${getToken()}`
      },
      body: JSON.stringify({
        imageBase64,
        metadataJson: JSON.stringify({ source: "search-page", fileName: file.name })
      })
    });
    const extData = await extRes.json();
    if (extData.code !== 0) {
      setResult(extData);
      return;
    }
    const feature = extData.data.feature;
    const seaRes = await fetch(`${apiBase}/api/vector/search`, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        Authorization: `Bearer ${getToken()}`
      },
      body: JSON.stringify({ feature, topK })
    });
    const seaData = await seaRes.json();
    setResult({ extract: extData, search: seaData });
  } catch (error) {
    setResult(`检索失败：${error.message}`);
  }
}

document.getElementById("runBtn").addEventListener("click", runSearch);
