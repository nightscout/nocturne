import type { Config } from "@sveltejs/kit/vite";

/** Where browsers post violation reports: the web app's own client error receiver. */
export const CSP_REPORT_URI = "/api/otel/errors";

/**
 * The report-only policy SvelteKit sends with a nonce added to `script-src` for each script it
 * emits. Each directive holds only what the app is known to need: the realtime socket and the API
 * proxy share the page origin, fonts are served from `/fonts`, and the watercolour engine compiles
 * wasm. Directives the app has no audited basis for are left out.
 */
export const REPORT_ONLY_DIRECTIVES = {
  "script-src": ["self", "wasm-unsafe-eval"],
  "object-src": ["none"],
  "base-uri": ["self"],
  "connect-src": ["self"],
  "font-src": ["self"],
  "report-uri": [CSP_REPORT_URI],
} satisfies NonNullable<NonNullable<Config["csp"]>["reportOnly"]>;

/** The `kit.csp` option: nothing is enforced, so no `directives` block exists. */
export const KIT_CSP = { mode: "auto", reportOnly: REPORT_ONLY_DIRECTIVES } as const;
