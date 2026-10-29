import { svelte } from "@sveltejs/vite-plugin-svelte";
import tailwindcss from "@tailwindcss/vite";
import { playwright } from "@vitest/browser-playwright";
import { defineConfig } from "vitest/config";

export default defineConfig({
  plugins: [svelte(), tailwindcss()],
  resolve: { dedupe: ["svelte", "layerchart"] },
  // pnpm's global store lives outside the workspace root; see the app's vitest.browser.config.ts.
  server: { fs: { strict: false } },
  optimizeDeps: { exclude: ["@lucide/svelte"], entries: ["src/**/*.svelte.test.ts"] },
  test: {
    include: ["src/**/*.test.ts"],
    setupFiles: ["vitest-browser-svelte"],
    browser: {
      enabled: true,
      provider: playwright(),
      headless: true,
      instances: [{ browser: "chromium" }],
    },
  },
});
