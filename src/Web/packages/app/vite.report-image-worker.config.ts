import { defineConfig } from "vite";
import { svelte, vitePreprocess } from "@sveltejs/vite-plugin-svelte";

/**
 * The report image worker (src/lib/server/report-image/worker.ts) as one Node module with the charts,
 * layerchart and Svelte's server renderer inside it: a worker thread runs plain Node, not the app's
 * Vite pipeline. The canvas stays external, a native addon resolved from the app's node_modules.
 */
export default defineConfig({
  plugins: [svelte({ preprocess: vitePreprocess(), compilerOptions: { experimental: { async: true } } })],
  resolve: { conditions: ["svelte", "node"] },
  ssr: { noExternal: true, external: ["@napi-rs/canvas"] },
  build: {
    ssr: "src/lib/server/report-image/worker.ts",
    outDir: "build/report-image-worker",
    emptyOutDir: true,
    target: "node24",
    rollupOptions: { output: { entryFileNames: "worker.mjs", format: "es" } },
  },
});
