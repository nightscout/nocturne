import { debt, nodeConfig } from "@nocturne/eslint-config";

export default [
  ...nodeConfig(),
  ...debt({
    "@typescript-eslint/no-explicit-any": 43,
    "@typescript-eslint/consistent-type-assertions": 14
  })
];
