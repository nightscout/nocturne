import { fileURLToPath } from "node:url";
import { defineConfig } from "vitest/config";
import { svelte } from "@sveltejs/vite-plugin-svelte";
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
  plugins: [svelte(), wuchale()],
  test: {
    // Its own suffix: `*.ssr.test.ts` already belongs to tests that run under
    // the node config, and those reach the generated API client, which the
    // job running this one does not build.
    include: ["src/**/*.render.test.ts"],
    environment: "node",
    alias: [
      {
        // Relative, so it cannot be matched by module name.
        find: /api\/generated\/nocturne-api-client$/,
        replacement: fileURLToPath(
          new URL("./src/lib/test-stubs/nocturne-api-client.ts", import.meta.url)
        ),
      },
      {
        // Before the $lib prefix, so it wins for this one module: the real
        // barrel re-exports formatting, which reaches the generated client.
        find: "$lib/utils",
        replacement: fileURLToPath(new URL("./src/lib/test-stubs/utils-cn.ts", import.meta.url)),
      },
      { find: /^\$lib\//, replacement: fileURLToPath(new URL("./src/lib/", import.meta.url)) },
      { find: "$lib", replacement: fileURLToPath(new URL("./src/lib", import.meta.url)) },
      {
        find: "$app/environment",
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
