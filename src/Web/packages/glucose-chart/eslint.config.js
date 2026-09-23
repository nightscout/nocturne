import { debt, svelteConfig } from "@nocturne/eslint-config";

export default [
  // No stylesheet here imports Tailwind: the consumer supplies it and one of src/themes,
  // so no-raw-colors flags palette colours but cannot check token names.
  ...svelteConfig(),
  ...debt({
    "shadcn/no-inline-styles": 21,
    "@typescript-eslint/consistent-type-assertions": 10,
    "svelte/require-each-key": 5,
    "shadcn/no-raw-colors": 5,
    "@typescript-eslint/no-explicit-any": 1,
    "svelte/prefer-svelte-reactivity": 1,
    "shadcn/no-arbitrary-values": 10,
    "no-restricted-syntax": 6
  })
];
