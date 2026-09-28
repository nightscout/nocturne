/**
 * The one-shot marker an accept flow (member invite or guest code) puts on the
 * URL it lands on, so the authenticated layout greets the newcomer once.
 */
export const WELCOME_PARAM = "welcome";

/** Owners hold full access (the seeded owner role); they never joined anyone. */
const FULL_ACCESS = "*";

/** `path` (a same-origin path, possibly with a query or hash) carrying the marker. */
export function withWelcome(path: string): string {
  const url = new URL(path, "http://welcome.invalid");
  url.searchParams.set(WELCOME_PARAM, "1");
  return `${url.pathname}${url.search}${url.hash}`;
}

/** `url` with the marker removed, or null when it carries none. */
export function takeWelcome(url: URL): URL | null {
  if (!url.searchParams.has(WELCOME_PARAM)) return null;
  const rest = new URL(url);
  rest.searchParams.delete(WELCOME_PARAM);
  return rest;
}

/** Whether a viewer holding `scopes` is someone to welcome at all. */
export function isWelcomeViewer(scopes: readonly string[]): boolean {
  return !scopes.includes(FULL_ACCESS);
}
