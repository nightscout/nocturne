import type { ReadonlyURL } from "$app/state";
import { withoutQueryParam, withQueryParam } from "#lib/navigation/query-param.js";
/**
 * The one-shot marker an accept flow (member invite or guest code) puts on the
 * URL it lands on, so the authenticated layout greets the newcomer once.
 */
export const WELCOME_PARAM = "welcome";

/** Owners hold full access (the seeded owner role); they never joined anyone. */
const FULL_ACCESS = "*";

/** `path` carrying the marker. */
export function withWelcome(path: string): string {
  return withQueryParam(path, WELCOME_PARAM, "1");
}

/** `url` with the marker removed, or null when it carries none. */
export function takeWelcome(url: ReadonlyURL): URL | null {
  return withoutQueryParam(url, WELCOME_PARAM);
}

/** Whether a viewer holding `scopes` is someone to welcome at all. */
export function isWelcomeViewer(scopes: readonly string[]): boolean {
  return !scopes.includes(FULL_ACCESS);
}
