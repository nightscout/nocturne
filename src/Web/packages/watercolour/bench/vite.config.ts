import { resolve } from 'node:path';
import { svelte } from '@sveltejs/vite-plugin-svelte';
import { defineConfig } from 'vite';

export default defineConfig({
  root: __dirname,
  base: './',
  plugins: [svelte({ configFile: resolve(__dirname, '../svelte.config.js') })],
  build: { outDir: 'dist', emptyOutDir: true, target: 'esnext', assetsInlineLimit: 0 },
  server: { fs: { strict: false } },
});
