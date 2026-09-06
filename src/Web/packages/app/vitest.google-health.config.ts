import { fileURLToPath } from "node:url";
import { svelte } from "@sveltejs/vite-plugin-svelte";
import tailwindcss from "@tailwindcss/vite";
import { playwright } from "@vitest/browser-playwright";
import { defineConfig } from "vitest/config";

const local = (path: string) => fileURLToPath(new URL(path, import.meta.url));

export default defineConfig({
  plugins: [svelte(), tailwindcss()],
  optimizeDeps: {
    include: [
      "lucide-svelte",
      "d3-scale",
      "@lucide/svelte/icons/check",
      "@lucide/svelte/icons/chevron-down",
      "@lucide/svelte/icons/chevron-up",
    ],
  },
  server: { fs: { strict: false } },
  resolve: {
    dedupe: ["svelte"],
    alias: [
      {
        find: /(?:\$lib\/utils\/|\.\/)data-type-labels$/,
        replacement: local("./src/lib/test-stubs/data-type-labels.ts"),
      },
      {
        find: "$lib/api/generated/googleHealths.generated.remote",
        replacement: local("./src/lib/test-stubs/google-health.ts"),
      },
      {
        find: "$app/paths",
        replacement: local("./src/lib/test-stubs/google-health-app-paths.ts"),
      },
      {
        find: /^\$app\/(navigation|environment|state)$/,
        replacement: local(
          "./src/lib/test-stubs/year-overview-runtime.svelte.ts"
        ),
      },
      {
        find: "$api/generated/dataOverviews.generated.remote",
        replacement: local("./src/lib/test-stubs/year-overview-remote.ts"),
      },
      ...[
        "$lib/stores/appearance-store.svelte",
        "$lib/hooks/date-params.svelte",
      ].map((find) => ({
        find,
        replacement: local(
          "./src/lib/test-stubs/year-overview-runtime.svelte.ts"
        ),
      })),
      {
        find: "$lib/components/reports/year-overview/YearHeatmap.svelte",
        replacement: local("./src/lib/test-stubs/YearHeatmap.test-stub.svelte"),
      },
      ...[
        "$lib/components/reports/year-overview/YearOverviewFilters.svelte",
        "$lib/components/reports/year-overview/DayDetailPanel.svelte",
        "$lib/components/reports/GlycemicRiskIndexChart.svelte",
      ].map((find) => ({
        find,
        replacement: local(
          "./src/lib/test-stubs/EmptyYearPanel.test-stub.svelte"
        ),
      })),
      { find: "$lib", replacement: local("./src/lib") },
    ],
  },
  test: {
    include: [
      "src/**/google-health-page.svelte.test.ts",
      "src/**/google-health-source.svelte.test.ts",
      "src/lib/connectors/google-health-error.test.ts",
      "src/lib/utils/metric-color-focus.test.ts",
      "src/**/color-focus-range.svelte.test.ts",
      "src/**/year-color-focus.svelte.test.ts",
    ],
    setupFiles: ["vitest-browser-svelte", "./vitest.browser.setup.ts"],
    browser: {
      enabled: true,
      headless: true,
      provider: playwright({
        launchOptions: { channel: process.env.NOCTURNE_TEST_BROWSER_CHANNEL },
      }),
      instances: [{ browser: "chromium" }],
    },
  },
});
