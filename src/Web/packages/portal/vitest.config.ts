import { defineConfig } from 'vitest/config';
import { resolve } from 'node:path';
import { appAliases } from './app-aliases';

// Deliberately not the SvelteKit plugin: these suites cover plain modules. The alert simulator's
// adapter imports app modules, and with them the generated API client, which CI generates first.
// `svelte-kit sync` still has to run first — the package tsconfig extends the one it writes.
export default defineConfig({
  plugins: [appAliases()],
  resolve: {
    alias: {
      $lib: resolve(import.meta.dirname, 'src/lib'),
    },
  },
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
