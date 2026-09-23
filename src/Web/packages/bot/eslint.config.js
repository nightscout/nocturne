import { debt, nodeConfig } from "@nocturne/eslint-config";

export default [
  ...nodeConfig(),
  ...debt({
    "@typescript-eslint/consistent-type-assertions": 6,
    "@typescript-eslint/no-explicit-any": 2,
    "security/detect-unsafe-regex": 1
  })
];
