export * from "./api-client.generated";

// Remote functions must be imported directly from their .generated.remote.ts
// files (e.g. "$api/generated/alerts.generated.remote") so SvelteKit's remote
// functions plugin can transform them into RPC stubs on the client.
