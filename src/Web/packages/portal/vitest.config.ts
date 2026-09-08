import { defineConfig } from 'vitest/config';
import { resolve } from 'node:path';

// Deliberately not the SvelteKit plugin: these suites cover plain modules, so they run without
// the generated API client, which this package's `check` needs and CI does not produce for it.
// `svelte-kit sync` still has to run first — the package tsconfig extends the one it writes.
export default defineConfig({
  resolve: {
    alias: {
      $lib: resolve(import.meta.dirname, 'src/lib'),
    },
  },
  test: {
    environment: 'node',
    include: ['src/**/*.test.ts'],
  },
});
