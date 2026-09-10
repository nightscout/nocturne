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
  optimizeDeps: { exclude: ["runed/kit"] },
  test: {
    include: ["src/**/*.svelte.test.ts"],
    setupFiles: ["vitest-browser-svelte", "./vitest.browser.setup.ts"],
    browser: {
      enabled: true,
      provider: playwright(),
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
