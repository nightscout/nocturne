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
