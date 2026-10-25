import { cpSync, readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { svelte } from "@sveltejs/vite-plugin-svelte";
import tailwindcss from "@tailwindcss/vite";

export const here = (path) => fileURLToPath(new URL(path, import.meta.url)).replaceAll("\\", "/");
export const app = here("../app/");
const stub = (name) => `${app}src/lib/test-stubs/${name}`;
const shim = here("src/layerchart/");

/** The fixture's capture instant. The clock reads it before any app module runs, so it carries nothing else. */
export function capturedAt(fixturePath) {
  const { capturedAt } = JSON.parse(readFileSync(fixturePath, "utf8"));
  return `export const capturedAt = ${Number(capturedAt)};`;
}

/**
 * The bundle a composition mounts: app pages outside SvelteKit, with every remote function
 * answering from a fixture, driven by the tour (`src/tour/`). `content` is the plugin that provides
 * `virtual:report` (Layout, Page, fixture, film) and `virtual:captured-at`; the composition lands
 * in `out`.
 */
export function baseConfig({ out, content, aliases = [], compilerOptions }) {
  return {
    plugins: [
      content,
      {
        // A remote module becomes stand-ins named after its exports, so no server code is bundled.
        name: "replay-remote",
        enforce: "pre",
        load(id) {
          const path = id.replaceAll("\\", "/");
          if (!path.startsWith(`${app}src/`) || !path.endsWith(".remote.ts")) return null;
          const exports = [...readFileSync(path, "utf8").matchAll(/^export const (\w+) = (query|command|form)\b/gm)];
          const kind = { query: "replayQuery", command: "replayCommand", form: "replayForm" };
          return [
            `import { replayQuery, replayCommand, replayForm } from "${here("src/replay/remote.js")}";`,
            ...exports.map(([, fn, type]) => `export const ${fn} = ${kind[type]}(${JSON.stringify(fn)});`),
          ].join("\n");
        },
      },
      {
        // Every layerchart import goes through the render-safe shim, except the shim's own.
        name: "layerchart-shim",
        enforce: "pre",
        resolveId(source, importer) {
          if (source !== "layerchart" || !importer || importer.replaceAll("\\", "/").startsWith(shim)) return null;
          return `${shim}index.js`;
        },
      },
      {
        // Svelte's JS transitions and animations finish at once (src/svelte/transition.js says why).
        name: "svelte-motion-stills",
        enforce: "pre",
        resolveId(source) {
          if (source === "svelte/transition") return here("./src/svelte/transition.js");
          if (source === "svelte/animate") return here("./src/svelte/animate.js");
          return null;
        },
      },
      {
        // app.css loads the fonts from the app's static root.
        name: "copy-fonts",
        closeBundle() {
          cpSync(`${app}static/fonts`, `${out}fonts`, { recursive: true });
        },
      },
      tailwindcss(),
      svelte(compilerOptions ? { compilerOptions } : {}),
    ],
    define: { "process.env.NODE_ENV": '"production"' },
    resolve: {
      dedupe: ["svelte", "layerchart"],
      alias: [
        { find: "$app/environment", replacement: stub("app-environment.ts") },
        // A film's own: reactive, so a page that keeps state in its URL re-renders, and never navigating.
        { find: "$app/navigation", replacement: here("src/replay/app-navigation.js") },
        { find: "$app/paths", replacement: stub("app-paths.ts") },
        { find: "$app/server", replacement: stub("app-server.ts") },
        { find: "$app/state", replacement: here("src/replay/app-state.svelte.js") },
        { find: "@sveltejs/kit", replacement: stub("sveltekit.ts") },
        { find: "$api-clients", replacement: `${app}src/lib/api/generated/nocturne-api-client` },
        { find: "$lib", replacement: `${app}src/lib` },
        { find: "$api", replacement: `${app}src/lib/api` },
        { find: "$routes", replacement: `${app}src/routes` },
        ...aliases,
      ],
    },
    build: {
      outDir: `${out}vendor`,
      emptyOutDir: true,
      cssCodeSplit: false,
      lib: {
        entry: here("src/tour/index.js"),
        name: "NocturneTour",
        formats: ["iife"],
        fileName: () => "report.js",
        cssFileName: "report",
      },
    },
  };
}
