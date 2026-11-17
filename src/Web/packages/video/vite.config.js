import { existsSync } from "node:fs";
import { defineConfig } from "vite";
import { reports } from "./src/reports.js";
import { app, baseConfig, capturedAt, here } from "./vite.base.js";

const name = process.env.VIDEO_REPORT;
if (!reports[name]) throw new Error(`VIDEO_REPORT must name a report: ${Object.keys(reports).join(", ")}`);
const routes = `${app}src/routes/(authenticated)/reports`;
const fixturePath = here(`src/fixtures/${name}.json`);
const filmPath = here(`src/films/${name}.js`);

/** A report composition's bundle: the app's own reports layout and page, and the report's film. */
export default defineConfig(
  baseConfig({
    out: here(`compositions/${name}/`),
    content: {
      name: "report",
      resolveId: (id) => (id === "virtual:report" || id === "virtual:captured-at" ? `\0${id}` : null),
      load(id) {
        if (id === "\0virtual:captured-at") return capturedAt(fixturePath);
        if (id !== "\0virtual:report") return null;
        return [
          `export { default as Layout } from "${routes}/+layout.svelte";`,
          `export { default as Page } from "${routes}/${reports[name].page ?? `${name}/+page.svelte`}";`,
          `export { default as fixture } from "${fixturePath}";`,
          existsSync(filmPath) ? `export { default as film } from "${filmPath}";` : "export const film = null;",
        ].join("\n");
      },
    },
  })
);
