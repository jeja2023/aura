/* 文件：登录页脚本（login.js） | File: Login Script */
const apiBase = "https://localhost:5001";

function getReturnUrl() {
  const params = new URLSearchParams(window.location.search);
  const raw = params.get("returnUrl");
  if (!raw) return "/index/";
  try {
    const decoded = decodeURIComponent(raw);
    if (!decoded.startsWith("/") || decoded.startsWith("//")) return "/index/";
    return decoded;
  } catch {
    return "/index/";
  }
}

function saveLoginState(token) {
  localStorage.setItem("token", token);
  document.cookie = `aura_token=${encodeURIComponent(token)}; path=/; SameSite=Lax`;
}

async function login() {
  const user = document.getElementById("user").value.trim();
  const pass = document.getElementById("pass").value.trim();
  const result = document.getElementById("result");

  try {
    const res = await fetch(`${apiBase}/api/auth/login`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ userName: user, password: pass })
    });
    const data = await res.json();
    if (data?.code === 0 && data?.data?.token) {
      saveLoginState(data.data.token);
      result.textContent = "登录成功，正在跳转...";
      window.location.href = getReturnUrl();
      return;
    }
    result.textContent = JSON.stringify(data, null, 2);
  } catch (error) {
    result.textContent = `登录失败：${error.message}`;
  }
}

function bootstrap() {
  const token = localStorage.getItem("token") ?? "";
  if (token) {
    document.cookie = `aura_token=${encodeURIComponent(token)}; path=/; SameSite=Lax`;
  }
  document.getElementById("submit").addEventListener("click", login);
}

bootstrap();
