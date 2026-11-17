import { svelte } from '@sveltejs/vite-plugin-svelte';
import { defineConfig } from 'vitest/config';

// Deliberately not the SvelteKit plugin: these suites cover plain modules, and the Svelte plugin
// is here only to compile the runes of a `.svelte.ts` module. The alert simulator's
// adapter imports app modules, and with them the generated API client, which CI generates first.
// `svelte-kit sync` still has to run first — the package tsconfig extends `$app/tsconfig`, which it writes.
export default defineConfig({
  plugins: [svelte()],
  test: {
    environment: 'node',
    include: ['src/**/*.test.ts'],
    // Off unless asked for (`--coverage`); CI collects it for the PR coverage report.
    coverage: {
      provider: 'v8',
      reporter: ['text-summary', 'json-summary', 'cobertura'],
      reportsDirectory: 'coverage',
      include: ['src/**/*.{ts,js}'],
      exclude: ['src/**/*.test.ts', 'src/**/*.test.svelte', 'src/**/test-stubs/**', 'src/lib/api/generated/**', '**/*.d.ts'],
    },
  },
});
