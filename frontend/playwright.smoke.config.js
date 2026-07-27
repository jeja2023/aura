const { defineConfig, devices } = require("@playwright/test");

module.exports = defineConfig({
  testDir: "./tests/smoke",
  testMatch: "**/*.spec.js",
  timeout: 30000,
  expect: { timeout: 5000 },
  fullyParallel: true,
  reporter: "line",
  use: {
    baseURL: "http://127.0.0.1:4173",
    trace: "retain-on-failure",
  },
  projects: [
    { name: "chromium", use: { ...devices["Desktop Chrome"] } },
    { name: "mobile-chromium", use: { ...devices["Pixel 7"] } },
  ],
  webServer: {
    command: "node tests/smoke/server.cjs",
    url: "http://127.0.0.1:4173/login/login.html",
    reuseExistingServer: true,
    timeout: 15000,
  },
});
