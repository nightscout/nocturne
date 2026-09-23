import { debt, svelteConfig } from "@nocturne/eslint-config";

export default [
  ...svelteConfig({
    // The design system owns its components' styling; callers are held to it elsewhere.
    componentDirs: ["src/lib/components/ui/**"],
    shadcnSettings: { ui: "$lib/components/ui" }
  }),
  ...debt({
    "@typescript-eslint/consistent-type-assertions": 15,
    "shadcn/no-unknown-classes": 3,
    "no-useless-assignment": 3,
    "svelte/no-unused-svelte-ignore": 1,
    "shadcn/no-inline-styles": 1,
    "security/detect-non-literal-regexp": 1
  })
];
