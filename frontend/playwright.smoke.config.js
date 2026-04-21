// 文件：前端冒烟测试配置（playwright.smoke.config.js） | File: Frontend smoke test config
const { defineConfig } = require("@playwright/test");

const useSystemChrome = !process.env.CI && process.env.AURA_SMOKE_USE_BUNDLED_BROWSER !== "1";

module.exports = defineConfig({
  testDir: "tests/smoke",
  timeout: 30_000,
  retries: 0,
  use: {
    baseURL: "http://127.0.0.1:4173",
    // 本地默认优先走系统 Chrome，CI 继续使用 Playwright 安装的浏览器。
    channel: useSystemChrome ? "chrome" : undefined,
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
    // 本地关闭视频，避免缺少 ffmpeg 时阻塞；CI 保留失败录像用于排查。
    video: process.env.CI ? "retain-on-failure" : "off"
  },
  webServer: {
    command: "node tests/smoke/server.js",
    url: "http://127.0.0.1:4173/healthz",
    reuseExistingServer: !process.env.CI,
    timeout: 30_000
  }
});
