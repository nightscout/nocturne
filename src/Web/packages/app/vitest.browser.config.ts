import { svelte } from "@sveltejs/vite-plugin-svelte";
import { playwright } from "@vitest/browser-playwright";
import tailwindcss from "@tailwindcss/vite";
import { defineConfig } from "vitest/config";

export default defineConfig({
  plugins: [svelte(), tailwindcss()],
  test: {
    include: ["src/**/*.svelte.test.ts"],
    setupFiles: ["vitest-browser-svelte", "./vitest.browser.setup.ts"],
    browser: {
      enabled: true,
      provider: playwright(),
      instances: [{ browser: "chromium" }],
    },
    alias: {
      "$app/environment": new URL(
        "./src/lib/test-stubs/app-environment.ts",
        import.meta.url
      ).pathname,
      "$app/navigation": new URL(
        "./src/lib/test-stubs/app-navigation.ts",
        import.meta.url
      ).pathname,
      "$app/server": new URL(
        "./src/lib/test-stubs/app-server.ts",
        import.meta.url
      ).pathname,
      "$app/state": new URL(
        "./src/lib/test-stubs/app-state.ts",
        import.meta.url
      ).pathname,
      "@sveltejs/kit": new URL(
        "./src/lib/test-stubs/sveltekit.ts",
        import.meta.url
      ).pathname,
      $lib: new URL("./src/lib", import.meta.url).pathname,
      $api: new URL("./src/lib/api/", import.meta.url).pathname,
      "$api-clients": new URL(
        "./src/lib/api/generated/nocturne-api-client",
        import.meta.url
      ).pathname,
      $routes: new URL("./src/routes", import.meta.url).pathname,
    },
  },
});
