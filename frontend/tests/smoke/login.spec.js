const { test, expect } = require("@playwright/test");

test("login surface renders without browser errors or horizontal overflow", async ({ page }) => {
  const errors = [];
  page.on("pageerror", (error) => errors.push(error.message));
  page.on("console", (message) => {
    if (message.type() === "error") errors.push(message.text());
  });

  const response = await page.goto("/login/login.html", { waitUntil: "networkidle" });

  expect(response?.ok()).toBeTruthy();
  await expect(page.getByRole("heading", { name: "系统登录" })).toBeVisible();
  await expect(page.locator("#user")).toBeEditable();
  await expect(page.locator("#pass")).toHaveAttribute("type", "password");
  await expect(page.getByRole("button", { name: "登录" })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBeTruthy();
  expect(errors).toEqual([]);
});
