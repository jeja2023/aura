/* 文件：楼层页脚本（floor.js） | File: Floor Script */
const apiBase = "https://localhost:5001";
const resultEl = document.getElementById("result");
const previewEl = document.getElementById("preview");

function getToken() {
  return localStorage.getItem("token") ?? "";
}

function setResult(data) {
  resultEl.textContent = typeof data === "string" ? data : JSON.stringify(data, null, 2);
}

async function uploadAndCreate() {
  const fileEl = document.getElementById("file");
  const file = fileEl.files?.[0];
  const nodeId = Number(document.getElementById("nodeId").value);
  const scaleRatio = Number(document.getElementById("scaleRatio").value);
  if (!file) {
    setResult("请先选择文件");
    return;
  }

  setResult("上传中...");
  try {
    const form = new FormData();
    form.append("file", file);
    const uploadRes = await fetch(`${apiBase}/api/floor/upload`, {
      method: "POST",
      headers: { Authorization: `Bearer ${getToken()}` },
      body: form
    });
    const uploadData = await uploadRes.json();
    if (uploadData.code !== 0) {
      setResult(uploadData);
      return;
    }

    const filePath = uploadData.data.filePath;
    previewEl.src = `${apiBase}${filePath}`;
    const createRes = await fetch(`${apiBase}/api/floor/create`, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        Authorization: `Bearer ${getToken()}`
      },
      body: JSON.stringify({ nodeId, filePath, scaleRatio })
    });
    const createData = await createRes.json();
    setResult({ upload: uploadData, create: createData });
  } catch (error) {
    setResult(`上传失败：${error.message}`);
  }
}

async function loadList() {
  setResult("加载中...");
  try {
    const res = await fetch(`${apiBase}/api/floor/list`, {
      headers: { Authorization: `Bearer ${getToken()}` }
    });
    const data = await res.json();
    setResult(data);
  } catch (error) {
    setResult(`查询失败：${error.message}`);
  }
}

document.getElementById("uploadBtn").addEventListener("click", uploadAndCreate);
document.getElementById("listBtn").addEventListener("click", loadList);
