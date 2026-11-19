import { resolve } from 'node:path';
import { svelte } from '@sveltejs/vite-plugin-svelte';
import { defineConfig } from 'vite';

// One ES module with its styles inside, written next to the other Scalar assets the API serves.
// Scalar imports it by URL (`pluginUrls`), so it can have no sibling chunks or stylesheet.
export default defineConfig({
  plugins: [svelte({ compilerOptions: { css: 'injected' } })],
  build: {
    lib: { entry: resolve(import.meta.dirname, 'src/index.ts'), formats: ['es'], fileName: () => 'nocturne-plugin.js' },
    outDir: resolve(import.meta.dirname, '../../../API/Nocturne.API/wwwroot/scalar'),
    emptyOutDir: false,
    target: 'es2022',
    // Also drops the module path comments, which name the local package store and would make the
    // tracked bundle differ from one machine to the next.
    rolldownOptions: { output: { minify: true } },
  },
});
