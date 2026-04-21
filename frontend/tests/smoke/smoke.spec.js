// 文件：前端主链路冒烟测试（smoke.spec.js） | File: Frontend critical path smoke tests
const { test, expect } = require("@playwright/test");
const crypto = require("crypto");

const jwtKey = "aura-integration-test-jwt-signing-key-min-32-chars";
const jwtIssuer = "Aura.Api.Testing";
const jwtAudience = "Aura.Client.Testing";
const mustChangePasswordClaimType = "aura:must_change_password";

function base64UrlEncodeJson(obj) {
  const json = JSON.stringify(obj);
  return Buffer.from(json, "utf8").toString("base64url");
}

function signHs256(message, key) {
  return crypto.createHmac("sha256", key).update(message).digest("base64url");
}

function createJwt({ userName, role, mustChangePassword }) {
  const header = { alg: "HS256", typ: "JWT" };
  const now = Math.floor(Date.now() / 1000);
  const payload = {
    iss: jwtIssuer,
    aud: jwtAudience,
    sub: userName,
    exp: now + 60 * 60,
    nbf: now - 5,
    iat: now,
    jti: crypto.randomUUID(),
    nameid: userName,
    unique_name: userName,
    role,
    [mustChangePasswordClaimType]: mustChangePassword ? "true" : "false"
  };
  const h = base64UrlEncodeJson(header);
  const p = base64UrlEncodeJson(payload);
  const msg = `${h}.${p}`;
  const sig = signHs256(msg, jwtKey);
  return `${msg}.${sig}`;
}

async function setAuthCookie(context, baseURL, { userName, role, mustChangePassword }) {
  const u = new URL(baseURL);
  await context.addCookies([
    {
      name: "aura_token",
      value: createJwt({ userName, role, mustChangePassword }),
      domain: u.hostname,
      path: "/",
      httpOnly: true,
      sameSite: "Lax"
    }
  ]);
}

test("登录页：登录成功后可跳转到首页", async ({ page }) => {
  await page.route("**/api/auth/login", async (route) => {
    await route.fulfill({
      status: 200,
      contentType: "application/json; charset=utf-8",
      body: JSON.stringify({
        code: 0,
        msg: "登录成功",
        data: { userName: "admin", role: "super_admin", mustChangePassword: false }
      })
    });
  });

  await page.goto("/login/");
  await page.locator("#user").fill("admin");
  await page.locator("#pass").fill("AnyPass#2026");
  await page.locator("#submit").click();
  await expect(page).toHaveURL(/\/index\//);
});

test("强制改密：登录后跳转改密页，改密成功返回首页", async ({ page }) => {
  await page.route("**/api/auth/login", async (route) => {
    await route.fulfill({
      status: 200,
      contentType: "application/json; charset=utf-8",
      body: JSON.stringify({
        code: 0,
        msg: "登录成功",
        data: { userName: "admin", role: "super_admin", mustChangePassword: true }
      })
    });
  });
  await page.route("**/api/auth/me", async (route) => {
    await route.fulfill({
      status: 200,
      contentType: "application/json; charset=utf-8",
      body: JSON.stringify({
        code: 0,
        msg: "查询成功",
        data: { userName: "admin", role: "super_admin", mustChangePassword: true }
      })
    });
  });
  await page.route("**/api/auth/change-password", async (route) => {
    await route.fulfill({
      status: 200,
      contentType: "application/json; charset=utf-8",
      body: JSON.stringify({ code: 0, msg: "密码修改成功", data: { mustChangePassword: false } })
    });
  });

  await page.goto("/login/?returnUrl=%2Findex%2F");
  await page.locator("#user").fill("admin");
  await page.locator("#pass").fill("TempPass#2026");
  await page.locator("#submit").click();
  await expect(page).toHaveURL(/\/password\/\?/);

  await page.locator("#currentPassword").fill("TempPass#2026");
  await page.locator("#newPassword").fill("NewPass#2026_Abc");
  await page.locator("#confirmPassword").fill("NewPass#2026_Abc");
  await page.locator("#submitPassword").click();
  await expect(page).toHaveURL(/\/index\//);
});

test("用户：可发起重置密码操作", async ({ page, context, baseURL }) => {
  await setAuthCookie(context, baseURL, { userName: "admin", role: "super_admin", mustChangePassword: false });

  await page.route("**/api/user/list**", async (route) => {
    await route.fulfill({
      status: 200,
      contentType: "application/json; charset=utf-8",
      body: JSON.stringify({
        code: 0,
        msg: "查询成功",
        data: [{ userId: 1, userName: "admin", displayName: "系统管理员", roleName: "super_admin", roleId: 1, status: 1, mustChangePassword: false }],
        pager: { page: 1, pageSize: 20, total: 1 }
      })
    });
  });
  await page.route("**/api/user/1/password", async (route) => {
    await route.fulfill({
      status: 200,
      contentType: "application/json; charset=utf-8",
      body: JSON.stringify({
        code: 0,
        msg: "已生成临时密码，用户下次登录需先修改密码",
        data: { mustChangePassword: true, temporaryPassword: "TempPass#2026" }
      })
    });
  });

  await page.goto("/user/");
  await page.getByRole("button", { name: "刷新用户" }).click();
  await page.locator('[data-user-action="reset-password"]').first().click();
  await expect(page.locator("#userModalResetPassword")).toBeVisible();
  await page.locator("#confirmResetPassword").click();
  await expect(page.locator("#userModalResetPassword")).toBeHidden();
});

test("首页：能加载状态与概览（不要求真实后端）", async ({ page, context, baseURL }) => {
  await setAuthCookie(context, baseURL, { userName: "admin", role: "super_admin", mustChangePassword: false });

  await page.route("**/api/health", async (route) => {
    await route.fulfill({
      status: 200,
      contentType: "application/json; charset=utf-8",
      body: JSON.stringify({ code: 0, msg: "正常", time: "2026-04-21T10:00:00+08:00" })
    });
  });
  await page.route("**/api/stats/overview", async (route) => {
    await route.fulfill({
      status: 200,
      contentType: "application/json; charset=utf-8",
      body: JSON.stringify({ code: 0, msg: "查询成功", data: { totalCapture: 1, totalAlert: 2, onlineDevice: 3 } })
    });
  });
  await page.route("**/hubs/events**", async (route) => {
    await route.abort();
  });

  await page.goto("/index/");
  await expect(page.locator("#statusBadge")).toHaveText("正常");
  await expect(page.locator("#overviewCapture")).toHaveText("1");
  await expect(page.locator("#overviewAlert")).toHaveText("2");
  await expect(page.locator("#overviewDevice")).toHaveText("3");
});

