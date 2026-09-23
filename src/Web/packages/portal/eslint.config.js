import { debt, svelteConfig } from "@nocturne/eslint-config";

export default [
  ...svelteConfig({ shadcnSettings: { ui: "@nocturne/ui/ui" } }),
  {
    // A shell snippet's `\n` has no equivalent in a quoted attribute.
    rules: { "svelte/no-useless-mustaches": ["error", { ignoreStringEscape: true }] }
  },
  ...debt({
    "shadcn/no-restyle": 50,
    "no-restricted-syntax": 19
  }),
  {
    // Build-time tooling over the repo's own files: every path is built from the package
    // root or a checked-in manifest, never from a request.
    files: ["scripts/**", "vite.config.ts"],
    rules: { "security/detect-non-literal-fs-filename": "off" }
  }
];
