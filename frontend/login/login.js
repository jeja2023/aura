/* 文件：登录页脚本（login.js） | File: Login Script */
const apiBase = "";

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

function buildPasswordRedirect(returnUrl) {
  const query = new URLSearchParams({ returnUrl });
  return `/password/?${query.toString()}`;
}

async function login() {
  const user = document.getElementById("user").value.trim();
  const pass = document.getElementById("pass").value.trim();
  const result = document.getElementById("result");
  result.hidden = true;
  result.textContent = "";

  try {
    const res = await fetch(`${apiBase}/api/auth/login`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      credentials: "include",
      body: JSON.stringify({ userName: user, password: pass })
    });
    const data = await res.json();
    if (data?.code === 0) {
      const returnUrl = getReturnUrl();
      const mustChangePassword = data?.data?.mustChangePassword === true;
      result.textContent = mustChangePassword ? "登录成功，正在前往修改密码页面..." : "登录成功，正在跳转...";
      result.hidden = true;
      window.location.href = mustChangePassword ? buildPasswordRedirect(returnUrl) : returnUrl;
      return;
    }
    result.textContent = data?.msg || "登录失败";
    result.hidden = false;
  } catch (error) {
    result.textContent = `登录失败：${error.message}`;
    result.hidden = false;
  }
}

function bootstrap() {
  document.getElementById("submit").addEventListener("click", login);
}

bootstrap();
