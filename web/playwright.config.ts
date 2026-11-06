import { defineConfig } from "@playwright/test";
export default defineConfig({
  testDir: "./e2e",
  fullyParallel: false,
  workers: 1,
  timeout: process.env.KITDESK_URL ? 120000 : 60000,
  expect: { timeout: process.env.KITDESK_URL ? 15000 : 5000 },
  use: {
    baseURL: process.env.KITDESK_URL ?? "http://127.0.0.1:5080",
    browserName: "chromium",
    navigationTimeout: process.env.KITDESK_URL ? 90000 : 30000,
    trace: "retain-on-failure",
    video: "on",
    viewport: { width: 1440, height: 1000 },
  },
  reporter: [["list"], ["html", { open: "never" }]],
});
