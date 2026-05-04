import { defineConfig } from "vitest/config";

export default defineConfig({
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
      $lib: new URL("./src/lib", import.meta.url).pathname,
      $api: new URL("./src/lib/api/", import.meta.url).pathname,
      "$api-clients": new URL(
        "./src/lib/api/generated/nocturne-api-client",
        import.meta.url
      ).pathname,
      $routes: new URL("./src/routes", import.meta.url).pathname,
      // mode-watcher only exports under "svelte" condition — stub for node tests
      "mode-watcher": new URL("./src/lib/test-stubs/mode-watcher.ts", import.meta.url).pathname,
    },
  },
});
