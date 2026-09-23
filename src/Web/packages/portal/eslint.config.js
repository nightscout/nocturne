import { debt, svelteConfig } from "@nocturne/eslint-config";

export default [
  ...svelteConfig({ shadcnSettings: { ui: "@nocturne/ui/ui" } }),
  {
    // A shell snippet's `\n` has no equivalent in a quoted attribute.
    rules: { "svelte/no-useless-mustaches": ["error", { ignoreStringEscape: true }] }
  },
  ...debt({
    "shadcn/no-arbitrary-values": 282,
    "shadcn/no-raw-colors": 76,
    "shadcn/no-inline-styles": 67,
    "svelte/no-navigation-without-resolve": 50,
    "@typescript-eslint/consistent-type-assertions": 14,
    "svelte/require-each-key": 10,
    "svelte/prefer-svelte-reactivity": 2,
    "svelte/no-at-html-tags": 1,
    "security/detect-non-literal-fs-filename": 1
  }),
  {
    // Build-time tooling over the repo's own files: every path is built from the package
    // root or a checked-in manifest, never from a request.
    files: ["scripts/**", "vite.config.ts"],
    rules: { "security/detect-non-literal-fs-filename": "off" }
  }
];
