/* 文件：改密页脚本（password.js） | File: Password Change Script */
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

function setResult(message, isError = false) {
  const result = document.getElementById("result");
  if (!result) return;
  result.hidden = !message;
  result.textContent = message || "";
  result.classList.toggle("is-error", Boolean(message) && isError);
}

async function loadCurrentSession() {
  const res = await fetch(`${apiBase}/api/auth/me`, {
    credentials: "include"
  });
  if (!res.ok) {
    throw new Error("登录状态已失效，请重新登录");
  }
  const payload = await res.json();
  return payload?.data || {};
}

async function changePassword() {
  const currentPassword = document.getElementById("currentPassword").value.trim();
  const newPassword = document.getElementById("newPassword").value.trim();
  const confirmPassword = document.getElementById("confirmPassword").value.trim();

  if (!currentPassword || !newPassword || !confirmPassword) {
    setResult("请完整填写当前密码与新密码", true);
    return;
  }

  if (newPassword !== confirmPassword) {
    setResult("两次输入的新密码不一致", true);
    return;
  }

  try {
    const res = await fetch(`${apiBase}/api/auth/change-password`, {
      method: "POST",
      credentials: "include",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ currentPassword, newPassword })
    });
    const payload = await res.json();
    if (!res.ok || payload?.code !== 0) {
      setResult(payload?.msg || `修改失败（HTTP ${res.status}）`, true);
      return;
    }

    setResult("密码修改成功，正在跳转...");
    window.location.href = getReturnUrl();
  } catch (error) {
    setResult(`修改失败：${error.message}`, true);
  }
}

async function logout() {
  try {
    await fetch(`${apiBase}/api/auth/logout`, {
      method: "POST",
      credentials: "include"
    });
  } catch {
    // 忽略登出异常，仍然回登录页
  }
  window.location.href = "/login/";
}

async function bootstrap() {
  document.getElementById("submitPassword").addEventListener("click", changePassword);
  document.getElementById("logoutButton").addEventListener("click", logout);

  try {
    const session = await loadCurrentSession();
    const userName = String(session?.userName || "").trim();
    const mustChangePassword = session?.mustChangePassword === true;
    const userEl = document.getElementById("passwordUserName");
    const leadEl = document.getElementById("passwordLead");

    if (userName && userEl) {
      userEl.hidden = false;
      userEl.textContent = `当前账号：${userName}`;
    }

    if (!mustChangePassword && leadEl) {
      leadEl.textContent = "你可以在这里更新当前账号密码，修改完成后将返回原页面。";
    }
  } catch (error) {
    setResult(error.message, true);
    window.setTimeout(() => {
      window.location.href = "/login/";
    }, 1200);
  }
}

bootstrap();
