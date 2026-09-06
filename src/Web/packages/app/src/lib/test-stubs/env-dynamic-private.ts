/**
 * Stub for $env/dynamic/private in the node test environment. SvelteKit reads
 * private env vars at call time from the process environment, so tests set
 * `process.env.*` and the module under test observes it.
 */
export const env: Record<string, string | undefined> = process.env;
