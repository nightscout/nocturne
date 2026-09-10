import { fileURLToPath } from "node:url";
import { defineConfig } from "vitest/config";
import { svelte } from "@sveltejs/vite-plugin-svelte";

export default defineConfig({
  plugins: [svelte()],
  test: {
    include: ["src/**/*.test.ts"],
    exclude: [
      "node_modules/**",
      "e2e/**",
      ".svelte-kit/**",
      "src/**/*.svelte.test.ts",
    ],
    environment: "node",
    alias: {
      $lib: fileURLToPath(new URL("./src/lib", import.meta.url)),
      $api: fileURLToPath(new URL("./src/lib/api/", import.meta.url)),
      "$api-clients": fileURLToPath(new URL(
        "./src/lib/api/generated/nocturne-api-client",
        import.meta.url
      )),
      $routes: fileURLToPath(new URL("./src/routes", import.meta.url)),
      // mode-watcher only exports under "svelte" condition — stub for node tests
      "mode-watcher": fileURLToPath(new URL("./src/lib/test-stubs/mode-watcher.ts", import.meta.url)),
      "$app/environment": fileURLToPath(new URL("./src/lib/test-stubs/app-environment-node.ts", import.meta.url)),
      // SvelteKit's env modules are virtual (provided by its vite plugin, which
      // isn't loaded here) — stub so server modules can be unit tested
      "$env/dynamic/private": fileURLToPath(new URL("./src/lib/test-stubs/env-dynamic-private.ts", import.meta.url)),
    },
  },
});
