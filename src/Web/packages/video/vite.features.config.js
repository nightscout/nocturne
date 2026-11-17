import { existsSync, readFileSync } from "node:fs";
import { defineConfig } from "vite";
import { features } from "./src/features.js";
import { app, baseConfig, capturedAt, here } from "./vite.base.js";

const name = process.env.VIDEO_FEATURE;
if (!features[name]) throw new Error(`VIDEO_FEATURE must name a feature: ${Object.keys(features).join(", ")}`);
const routes = `${app}src/routes/(authenticated)`;
const source = features[name].fixture ?? name;
const fixturePath = here(`src/fixtures/features/${source}.json`);
const filmPath = here(`src/features/films/${name}.js`);

/**
 * A feature composition's bundle: the feature's app pages (or its stage) stacked under one root,
 * with `src/features/Layout.svelte` and `Pages.svelte` standing in for the reports layout and page.
 * `virtual:feature` tells `Pages.svelte` what to stack.
 */
export default defineConfig(
  baseConfig({
    out: here(`compositions/feature-${name}/`),
    content: {
      name: "feature",
      resolveId: (id) => (["virtual:report", "virtual:feature", "virtual:captured-at"].includes(id) ? `\0${id}` : null),
      load(id) {
        if (id === "\0virtual:captured-at") return capturedAt(fixturePath);
        if (id === "\0virtual:feature") {
          const { params = {} } = JSON.parse(readFileSync(fixturePath, "utf8"));
          const { stage } = features[name];
          const pages = stage
            ? [here(`src/features/stages/${stage}`)]
            : features[source].pages.map(({ page }) => `${routes}/${page}`);
          return [
            ...pages.map((path, i) => `import Page${i} from "${path}";`),
            `export const pages = [${pages.map((_, i) => `Page${i}`).join(", ")}];`,
            `export const params = ${JSON.stringify(params)};`,
          ].join("\n");
        }
        if (id !== "\0virtual:report") return null;
        return [
          `export { default as Layout } from "${here("src/features/Layout.svelte")}";`,
          `export { default as Page } from "${here("src/features/Pages.svelte")}";`,
          `export { default as fixture } from "${fixturePath}";`,
          existsSync(filmPath) ? `export { default as film } from "${filmPath}";` : "export const film = null;",
        ].join("\n");
      },
    },
    aliases: [
      // The stages in src/features/stages draw Lucide icons, which this package does not depend on.
      { find: /^@lucide\/svelte\/icons\/(.+)$/, replacement: `${app}node_modules/@lucide/svelte/dist/icons/$1.svelte` },
    ],
    // As the app compiles: some feature pages `await` in their markup.
    compilerOptions: { experimental: { async: true } },
  })
);
