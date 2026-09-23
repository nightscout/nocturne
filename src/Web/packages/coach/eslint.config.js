import { SHADCN_FILES, debt, svelteConfig } from "@nocturne/eslint-config";

export default [
  ...svelteConfig(),
  {
    // Coach styles itself with its own plain CSS (theme.css, BEM classes and --coach-*
    // properties) and does not use Tailwind, so the Tailwind class rules have nothing to check.
    files: SHADCN_FILES,
    rules: {
      "shadcn/no-restyle": "off",
      "shadcn/no-raw-colors": "off",
      "shadcn/no-arbitrary-values": "off",
      "shadcn/no-unknown-classes": "off",
      "shadcn/require-static-classes": "off"
    }
  },
  ...debt({
    "svelte/prefer-svelte-reactivity": 11,
    "svelte/no-unused-svelte-ignore": 8,
    "shadcn/no-inline-styles": 1
  })
];
