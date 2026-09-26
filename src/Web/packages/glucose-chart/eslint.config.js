import { svelteConfig } from "@nocturne/eslint-config";

export default [
  // No stylesheet here imports Tailwind: the consumer supplies it and one of src/themes,
  // so no-raw-colors flags palette colours but cannot check token names.
  ...svelteConfig({
    // A published package that depends on no component library: its legend draws its own
    // toggle, and every other control comes from the consumer's button snippet.
    rawButtonFiles: ["src/controls/**/*.svelte"]
  })
];
