import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    include: ["src/api/**/*.e2e-spec.ts"],
    globalSetup: ["src/api/global-setup.ts"],
    // Each spec file seeds its own tenants, so files run in parallel; the stack, not the runner,
    // is the bottleneck, and two workers keep it busy without starving a laptop.
    pool: "threads",
    maxWorkers: Number(process.env.E2E_API_WORKERS ?? 2),
    fileParallelism: true,
    testTimeout: 60_000,
    hookTimeout: 120_000,
    reporters: process.env.CI ? ["default", "github-actions"] : ["default"],
  },
});
