import type { ReadonlyURL } from "$app/state";

/**
 * `path` with `name=value` in its query, in place of any value it already carried. The path text
 * is kept verbatim: parsing and re-serialising it would collapse dot segments, turning `/.//host`
 * into the protocol-relative `//host`.
 */
export function withQueryParam(path: string, name: string, value: string): string {
  const hashAt = path.indexOf("#");
  const head = hashAt < 0 ? path : path.slice(0, hashAt);
  const hash = hashAt < 0 ? "" : path.slice(hashAt);
  const queryAt = head.indexOf("?");
  const pathname = queryAt < 0 ? head : head.slice(0, queryAt);
  const kept = queryAt < 0
    ? []
    : head
        .slice(queryAt + 1)
        .split("&")
        .filter((pair) => pair !== "" && new URLSearchParams(pair).keys().next().value !== name);
  const pair = `${encodeURIComponent(name)}=${encodeURIComponent(value)}`;
  return `${pathname}?${[...kept, pair].join("&")}${hash}`;
}

/** `url` without `name`, or null when it carries none. */
export function withoutQueryParam(url: ReadonlyURL, name: string): URL | null {
  if (!url.searchParams.has(name)) return null;
  const rest = new URL(url.href);
  rest.searchParams.delete(name);
  return rest;
}
