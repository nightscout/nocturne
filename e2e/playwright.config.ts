import { availableParallelism } from "node:os";
import { defineConfig, devices } from "@playwright/test";

export default defineConfig({
  testDir: "src/web",
  testMatch: "**/*.e2e-spec.ts",
  globalSetup: "./src/web/global-setup.ts",
  fullyParallel: true,
  // Every test seeds its own tenant, so tests run in parallel; half the cores, at least two.
  workers: Number(process.env.E2E_WEB_WORKERS ?? Math.max(2, Math.floor(availableParallelism() / 2))),
  retries: process.env.CI ? 1 : 0,
  timeout: 60_000,
  expect: { timeout: 15_000 },
  forbidOnly: !!process.env.CI,
  reporter: process.env.CI
    ? [["github"], ["html", { open: "never", outputFolder: "playwright-report" }]]
    : [["list"], ["html", { open: "never", outputFolder: "playwright-report" }]],
  use: {
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
    video: "retain-on-failure",
    // Tenants live on http://<slug>.nocturne.localhost:<port>; the session cookies are Secure,
    // which Chromium honours over http on *.localhost as a potentially trustworthy origin.
    ignoreHTTPSErrors: true,
  },
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"] } }],
});
