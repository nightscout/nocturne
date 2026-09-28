/** A copy of `url` with the `name` search param set, or removed when `value` is null. */
export function withSearchParam(url: URL, name: string, value: string | null): URL {
  const next = new URL(url);
  if (value === null) next.searchParams.delete(name);
  else next.searchParams.set(name, value);
  return next;
}
