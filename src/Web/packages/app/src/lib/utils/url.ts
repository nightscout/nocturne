import type { ReadonlyURL } from "$app/state";
/**
 * A copy of `url` with the `name` search param set, or removed when `value` is
 * null.
 */
export function withSearchParam(
  url: ReadonlyURL,
  name: string,
  value: string | null
): URL {
  const next = new URL(url.href);
  if (value === null) next.searchParams.delete(name);
  else next.searchParams.set(name, value);
  return next;
}

/** A path segment this long is taken to be a token, which some sites put in the path to skip sign-in. */
const LONG_SEGMENT = 16;

/**
 * Host and path, with each long path segment cut to its first four characters so a list does not
 * show the credential. A value that is not a web address comes back unchanged.
 */
export function maskedSourceUrl(value: string): string {
  let url: URL;
  try {
    url = new URL(value);
  } catch {
    return value;
  }
  if (url.protocol !== "https:" && url.protocol !== "http:") return value;
  const path = url.pathname
    .split("/")
    .map((segment) => (segment.length >= LONG_SEGMENT ? `${segment.slice(0, 4)}…` : segment))
    .join("/")
    .replace(/\/+$/, "");
  return `${url.host}${path}`;
}

/**
 * `value` when it is an absolute `https:` URL, else undefined. For a link whose
 * address comes from the API or a third party, so a `javascript:` or `http:`
 * address is never rendered as a link.
 */
export function httpsHref(
  value: string | null | undefined
): string | undefined {
  if (!value) return undefined;
  try {
    return new URL(value).protocol === "https:" ? value : undefined;
  } catch {
    return undefined;
  }
}
