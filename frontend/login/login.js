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

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

async function verifySessionReady() {
  // 少量重试：部分环境下 Cookie 写入/协议切换可能有轻微延迟
  for (let i = 0; i < 3; i++) {
    try {
      const res = await fetch(`${apiBase}/api/auth/me`, { credentials: "include" });
      if (res.ok) return true;
    } catch {
      // ignore
    }
    await sleep(120);
  }
  return false;
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
      result.hidden = false;

      const ok = await verifySessionReady();
      if (!ok) {
        result.textContent =
          "登录接口返回成功，但浏览器未能建立登录状态（Cookie 未生效）。请确认使用同一协议与同一域名访问（建议全程 https），并检查浏览器/代理是否拦截了 Cookie。";
        result.hidden = false;
        return;
      }

      window.location.replace(mustChangePassword ? buildPasswordRedirect(returnUrl) : returnUrl);
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
