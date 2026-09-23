import { debt, svelteConfig } from "@nocturne/eslint-config";

export default [
  ...svelteConfig({ shadcnSettings: { ui: "@nocturne/ui/ui" } }),
  ...debt({
    "svelte/no-navigation-without-resolve": 6,
    "@typescript-eslint/consistent-type-assertions": 5,
    "shadcn/no-raw-colors": 3,
    "shadcn/no-inline-styles": 2,
    "shadcn/no-restyle": 21,
    "no-restricted-syntax": 3
  })
];
