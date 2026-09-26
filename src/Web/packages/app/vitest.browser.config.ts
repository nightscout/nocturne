import { fileURLToPath } from "node:url";
import { svelte } from "@sveltejs/vite-plugin-svelte";
import { playwright } from "@vitest/browser-playwright";
import tailwindcss from "@tailwindcss/vite";
import { defineConfig } from "vitest/config";

export default defineConfig({
  plugins: [svelte(), tailwindcss()],
  resolve: { dedupe: ["svelte", "@internationalized/date", "bits-ui"] },
  // pnpm 11's global links store lives outside the workspace root, so vite's strict fs
  // allow-list blocks serving vitest-browser-svelte to the browser runner.
  server: { fs: { strict: false } },
  // runed/kit imports `$app/*`, which esbuild's dep pre-bundler can't resolve —
  // the stubs below are vitest aliases, applied only in vite's own pipeline.
  // `entries` makes the dep optimizer crawl every test file before the run. Left to discover deps
  // as files load, it re-bundles mid-run and reloads the browser, and a test file whose import was
  // in flight then fails with "Failed to fetch dynamically imported module".
  optimizeDeps: { exclude: ["runed/kit"], entries: ["src/**/*.svelte.test.ts"] },
  test: {
    include: ["src/**/*.svelte.test.ts"],
    setupFiles: ["vitest-browser-svelte", "./vitest.browser.setup.ts"],
    // Two pages finish as fast as the default one-per-core here; the Vite server is the bottleneck.
    maxWorkers: 2,
    // Off unless asked for (`--coverage`); CI collects it for the PR coverage report. No `include`:
    // only files the browser loaded are reported, since the unit suite's report already lists every
    // source file (untested ones at zero) and instrumenting the rest here doubles time and memory.
    coverage: {
      provider: "v8",
      reporter: ["text-summary", "json-summary", "cobertura"],
      reportsDirectory: "coverage/browser",
      exclude: ["src/**/*.test.ts", "src/**/*.test.svelte", "src/**/test-stubs/**", "src/lib/api/generated/**", "**/node_modules/**", "**/.svelte-kit/**"],
    },
    browser: {
      enabled: true,
      provider: playwright(),
      // Always headless: the default follows process.env.CI and opens a visible window locally.
      headless: true,
      instances: [{ browser: "chromium" }],
    },
    alias: {
      "$app/environment": fileURLToPath(new URL(
        "./src/lib/test-stubs/app-environment.ts",
        import.meta.url
      )),
      "$app/navigation": fileURLToPath(new URL(
        "./src/lib/test-stubs/app-navigation.ts",
        import.meta.url
      )),
      "$app/paths": fileURLToPath(new URL(
        "./src/lib/test-stubs/app-paths.ts",
        import.meta.url
      )),
      "$app/server": fileURLToPath(new URL(
        "./src/lib/test-stubs/app-server.ts",
        import.meta.url
      )),
      "$app/state": fileURLToPath(new URL(
        "./src/lib/test-stubs/app-state.ts",
        import.meta.url
      )),
      "@sveltejs/kit": fileURLToPath(new URL(
        "./src/lib/test-stubs/sveltekit.ts",
        import.meta.url
      )),
      $lib: fileURLToPath(new URL("./src/lib", import.meta.url)),
      $api: fileURLToPath(new URL("./src/lib/api/", import.meta.url)),
      "$api-clients": fileURLToPath(new URL(
        "./src/lib/api/generated/nocturne-api-client",
        import.meta.url
      )),
      $routes: fileURLToPath(new URL("./src/routes", import.meta.url)),
    },
  },
});
