import { defineConfig } from 'vitest/config';

export default defineConfig({
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
