export const DEMO_ENABLED = import.meta.env.VITE_DEMO_ENABLED === "true";
export const DEMO_WEB_URL = import.meta.env.VITE_DEMO_WEB_URL || "";

// Base URL of the hosted Nocturne API that serves the OpenAPI specs consumed by
// the embedded Scalar reference at /scalar. Defaults to the production deployment.
export const SCALAR_API_URL = import.meta.env.VITE_SCALAR_API_URL || "https://nocturne.run";
