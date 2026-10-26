import { fileURLToPath } from "node:url";
import { defineConfig } from "vitest/config";
import { svelte, vitePreprocess } from "@sveltejs/vite-plugin-svelte";
import { wuchale } from "wuchale/vite";

/**
 * Server rendering, with the translation transform in the pipeline.
 *
 * The other suites compile the app or call its components directly; neither
 * renders a page through the transform wuchale applies. A transform can leave
 * the build green and still change what runs at render time, which is how the
 * sidebar provider's setContext stopped running on the server. These tests
 * render, so they see it.
 */
export default defineConfig({
  plugins: [
    svelte({ preprocess: vitePreprocess(), compilerOptions: { experimental: { async: true } } }),
    wuchale(),
  ],
  test: {
    // Its own suffix: `*.ssr.test.ts` already belongs to tests that run under
    // the node config without the production translation transform.
    include: ["src/**/*.render.test.ts"],
    environment: "node",
    // As in the app's SSR build (vite.config.ts `ssr.noExternal`): layerchart's and @layerstack's
    // dist import `svelte` without declaring it, which Node cannot resolve from pnpm's store, so
    // they go through Vite. Matched against the resolved path.
    server: { deps: { inline: [/\/layerchart\//, /\/@layerstack\//] } },
    alias: [
      {
        // The real barrel re-exports formatting, which reaches the generated client.
        find: /^#lib\/utils\/index\.js$/,
        replacement: fileURLToPath(new URL("./src/lib/test-stubs/utils-cn.ts", import.meta.url)),
      },
      // Before `$app/env`, which would otherwise match them as a prefix.
      {
        find: /^\$app\/env\/(private|public)$/,
        replacement: fileURLToPath(new URL("./src/lib/test-stubs/app-env-vars.ts", import.meta.url)),
      },
      {
        find: "$app/env",
        replacement: fileURLToPath(
          new URL("./src/lib/test-stubs/app-environment-node.ts", import.meta.url)
        ),
      },
      {
        find: "$app/navigation",
        replacement: fileURLToPath(
          new URL("./src/lib/test-stubs/app-navigation.ts", import.meta.url)
        ),
      },
      {
        find: "$app/state",
        replacement: fileURLToPath(new URL("./src/lib/test-stubs/app-state.ts", import.meta.url)),
      },
    ],
  },
});
