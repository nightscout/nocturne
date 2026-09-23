import { svelteConfig } from "@nocturne/eslint-config";

export default [
  ...svelteConfig({
    shadcnSettings: { ui: "@nocturne/ui/ui" },
    // A picture of the app's sign-in page, drawn in each provider's own colours; its
    // buttons are not focusable and do nothing.
    rawButtonFiles: ["src/lib/components/features/AuthDemo.svelte"],
    noRestyle: {
      allow: ["layout"],
      // A later matching contract replaces an earlier one, so each restates layout.
      contracts: [
        // Both draw nothing. The trigger lays out the caller's own children; the content
        // pads a rule it is given below the trigger row.
        { pattern: "^CollapsibleTrigger$", allow: ["layout", "gap"] },
        { pattern: "^CollapsibleContent$", allow: ["layout", "spacing", "border-t"] }
      ]
    }
  }),
  {
    // A shell snippet's `\n` has no equivalent in a quoted attribute.
    rules: { "svelte/no-useless-mustaches": ["error", { ignoreStringEscape: true }] }
  },
  {
    // Build-time tooling over the repo's own files: every path is built from the package
    // root or a checked-in manifest, never from a request.
    files: ["scripts/**", "vite.config.ts"],
    rules: { "security/detect-non-literal-fs-filename": "off" }
  }
];
