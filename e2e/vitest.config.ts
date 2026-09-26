import { availableParallelism } from "node:os";
import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    include: ["src/api/**/*.e2e-spec.ts"],
    globalSetup: ["src/api/global-setup.ts"],
    // Each spec file seeds its own tenants, so files run in parallel. Half the cores (at least
    // two): the stack, not the runner, is the bottleneck.
    pool: "threads",
    maxWorkers: Number(process.env.E2E_API_WORKERS ?? Math.max(2, Math.floor(availableParallelism() / 2))),
    fileParallelism: true,
    testTimeout: 60_000,
    hookTimeout: 120_000,
    reporters: process.env.CI ? ["default", "github-actions"] : ["default"],
  },
});
