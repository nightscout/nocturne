// Prerendered as a directory (`float/index.html`) so the Tauri overlay window can open `float/`
// in both dev and prod: the Vite dev server serves the `/float/` route, and adapter-static emits
// `float/index.html` that Tauri's asset protocol resolves for the same `float/` path. (A bare
// `float.html` would 404 against the dev server, which serves routes, not files.) All content is
// fetched client-side in onMount, so the prerendered output is just the shell.
export const prerender = true;
export const ssr = false;
export const trailingSlash = "always";
