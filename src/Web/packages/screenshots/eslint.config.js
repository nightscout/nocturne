import { nodeConfig } from "@nocturne/eslint-config";

export default [
  ...nodeConfig(),
  {
    // A CLI over the repo's own docs and images: every path is built from the repo root
    // or the checked-in manifest, never from a request.
    rules: { "security/detect-non-literal-fs-filename": "off" }
  }
];
