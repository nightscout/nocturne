import { sveltekit } from "@sveltejs/kit/vite";
import tailwindcss from "@tailwindcss/vite";
import { defineConfig, searchForWorkspaceRoot } from "vite";

export default defineConfig({
  plugins: [tailwindcss(), sveltekit()],
  // Tauri expects a fixed dev port (tauri.conf.json devUrl).
  server: {
    port: 1420,
    strictPort: true,
    // Don't watch the Rust build tree — Vite's watcher hits EBUSY on cargo's
    // locked build artifacts during a debug compile and crashes the dev server.
    watch: {
      ignored: ["**/src-tauri/**"],
    },
    fs: {
      allow: [searchForWorkspaceRoot(process.cwd())],
      strict: false, // pnpm symlinks into its content-addressable store
    },
  },
  // Per-icon imports would each be a new dep, re-optimized with a reload on first render.
  optimizeDeps: { exclude: ["@lucide/svelte"] },
  clearScreen: false,
});
