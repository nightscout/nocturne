/**
 * Stub for `$app/env/private` and `$app/env/public` in the node test environment. It exports
 * nothing, so every variable a module imports reads `undefined`. A test that needs a value mocks
 * the module with a getter, which is read at each access: `vi.mock("$app/env/private", () =>
 * ({ get INSTANCE_KEY() { return process.env.INSTANCE_KEY; } }))`.
 */
export {};
