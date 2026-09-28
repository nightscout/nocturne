import { availableParallelism } from "node:os";
import { fileURLToPath } from "node:url";
import { defineConfig } from "vitest/config";
import { svelte } from "@sveltejs/vite-plugin-svelte";

export default defineConfig({
  plugins: [svelte()],
  test: {
    include: ["src/**/*.test.ts", "tools/**/*.test.js"],
    exclude: [
      "node_modules/**",
      "e2e/**",
      ".svelte-kit/**",
      "src/**/*.svelte.test.ts",
      // SSR render tests need SvelteKit's virtual modules; they run under vitest.render.config.ts.
      "src/**/*.render.test.ts",
    ],
    environment: "node",
    // Half the cores, between two and four: measured on 10 cores, four workers finish in 23 s,
    // five take no less and add memory, two take 33 s.
    maxWorkers: Math.max(2, Math.min(4, Math.floor(availableParallelism() / 2))),
    // Off unless asked for (`--coverage`); CI collects it for the PR coverage report.
    coverage: {
      provider: "v8",
      reporter: ["text-summary", "json-summary", "cobertura"],
      reportsDirectory: "coverage/unit",
      include: ["src/**/*.{ts,js,svelte}"],
      exclude: ["src/**/*.test.ts", "src/**/*.test.svelte", "src/**/test-stubs/**", "src/lib/api/generated/**", "src/**/*.generated.*", "**/*.d.ts"],
    },
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
