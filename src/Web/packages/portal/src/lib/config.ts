export const DEMO_ENABLED = import.meta.env.VITE_DEMO_ENABLED === "true";
export const DEMO_WEB_URL = import.meta.env.VITE_DEMO_WEB_URL || "";

// Base URL of the hosted Nocturne API that serves the OpenAPI specs consumed by
// the embedded Scalar reference at /scalar. Defaults to the production deployment.
export const SCALAR_API_URL = import.meta.env.VITE_SCALAR_API_URL || "https://nocturne.run";

// Plausible analytics. Empty domain disables tracking entirely — the layout then injects no
// script tag and the browser makes no request — so only the deploy workflow turns it on.
// The host is absolute because GitHub Pages cannot proxy it first-party (see analytics.ts).
export const PLAUSIBLE_DOMAIN = import.meta.env.VITE_PLAUSIBLE_DOMAIN || "";
export const PLAUSIBLE_HOST =
  import.meta.env.VITE_PLAUSIBLE_HOST || "https://stats.nocturne.run";
