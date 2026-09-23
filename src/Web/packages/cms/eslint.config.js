import { debt, svelteConfig } from "@nocturne/eslint-config";

export default [
  // components.json points the theme at @nocturne/ui's, which portal renders cms with.
  ...svelteConfig({ shadcnSettings: { ui: "@nocturne/ui/ui" } }),
  {
    // Build-time tooling over the repo's own files: every path is built from the package
    // root or a checked-in manifest, never from a request.
    files: ["src/blog/vite-plugin.ts", "src/email/build.ts"],
    rules: { "security/detect-non-literal-fs-filename": "off" }
  },
  ...debt({
    "@typescript-eslint/consistent-type-assertions": 117,
    "@typescript-eslint/no-explicit-any": 24,
    "shadcn/no-inline-styles": 18,
    "shadcn/no-unknown-classes": 9,
    "svelte/no-at-html-tags": 3,
    "svelte/require-each-key": 3,
    "no-useless-assignment": 3,
    "security/detect-possible-timing-attacks": 1,
    "shadcn/no-raw-colors": 1,
    "shadcn/require-static-classes": 6,
    "security/detect-unsafe-regex": 2,
    "shadcn/no-arbitrary-values": 1
  })
];
