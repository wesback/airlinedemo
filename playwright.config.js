const { defineConfig } = require("@playwright/test");

module.exports = defineConfig({
  testDir: "tests/browser",
  timeout: 15000,
  use: {
    baseURL: "http://127.0.0.1:4173",
    browserName: "chromium",
    headless: true,
    launchOptions: {
      executablePath: process.env.CHROME_BIN || "/usr/bin/google-chrome"
    }
  },
  webServer: {
    command: "node tests/browser/server.mjs",
    port: 4173,
    reuseExistingServer: false
  }
});
