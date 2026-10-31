import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    include: ["src/**/*.test.ts"],
    environment: "node",
    // The static theme reads the stylesheets as text (`?raw`), which vitest empties unless CSS is processed.
    css: { include: [/\.css\?raw$/] },
  },
});
