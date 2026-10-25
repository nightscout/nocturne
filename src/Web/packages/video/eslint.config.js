import { svelteConfig } from "@nocturne/eslint-config";

export default [
  ...svelteConfig({ ignores: ["compositions/**"] }),
  {
    // Build and capture scripts over this package's own files: every path is built from the
    // package root or the report registry, never from a request.
    files: ["scripts/**", "vite.*.js"],
    rules: { "security/detect-non-literal-fs-filename": "off" },
  },
];
