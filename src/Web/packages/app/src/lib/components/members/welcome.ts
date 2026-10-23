/**
 * The one-shot marker an accept flow (member invite or guest code) puts on the
 * URL it lands on, so the authenticated layout greets the newcomer once.
 */
export const WELCOME_PARAM = "welcome";

/** Owners hold full access (the seeded owner role); they never joined anyone. */
const FULL_ACCESS = "*";

/**
 * `path` carrying the marker. The path text is kept verbatim: parsing and
 * re-serialising it would collapse dot segments, turning `/.//host` into the
 * protocol-relative `//host`.
 */
export function withWelcome(path: string): string {
  const hashAt = path.indexOf("#");
  const head = hashAt < 0 ? path : path.slice(0, hashAt);
  const hash = hashAt < 0 ? "" : path.slice(hashAt);
  const separator = !head.includes("?")
    ? "?"
    : head.endsWith("?") || head.endsWith("&")
      ? ""
      : "&";
  return `${head}${separator}${WELCOME_PARAM}=1${hash}`;
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
