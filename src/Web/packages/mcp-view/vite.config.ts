import { svelte } from "@sveltejs/vite-plugin-svelte";
import tailwindcss from "@tailwindcss/vite";
import { defineConfig, type Plugin } from "vite";
import { viteSingleFile } from "vite-plugin-singlefile";

/** The API embeds dist/report.html (Nocturne.API.csproj, BuildMcpView). */
function renameToReport(): Plugin {
  return {
    name: "nocturne-report-file-name",
    enforce: "post",
    generateBundle(_options, bundle) {
      const page = bundle["mcp-app.html"];
      if (page) page.fileName = "report.html";
    },
  };
}

export default defineConfig({
  plugins: [svelte(), tailwindcss(), viteSingleFile(), renameToReport()],
  resolve: { dedupe: ["svelte", "layerchart"] },
  build: {
    outDir: "dist",
    emptyOutDir: true,
    rollupOptions: {
      input: "mcp-app.html",
      // Inlining layerchart's dynamic imports into one file makes rolldown initialise those
      // modules lazily, and a component then ran before its module had (blank plot, "reading
      // 'has'"). scripts/render-smoke.mjs renders the built file to catch it.
      output: { strictExecutionOrder: true },
    },
  },
});
