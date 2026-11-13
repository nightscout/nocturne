import { svelte } from "@sveltejs/vite-plugin-svelte";
import { defineConfig } from "vitest/config";

export default defineConfig({
  // Component tests render on the server (`svelte/server`), so they need the Svelte compiler.
  plugins: [svelte()],
  test: {
    include: ["src/**/*.test.ts"],
    environment: "node",
    // Layerchart's dependencies import svelte from the shared pnpm store, outside this package's
    // reach; bundled here they resolve it from the package.
    server: { deps: { inline: [/layerchart/, /@layerstack/] } },
    // The static theme reads the stylesheets as text (`?raw`), which vitest empties unless CSS is processed.
    css: { include: [/\.css\?raw$/] },
  },
});
