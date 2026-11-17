import { stringify } from "devalue";

/** Plain objects with their keys sorted, as SvelteKit sorts a remote function's argument. */
function sorted(value) {
  if (Array.isArray(value)) return value.map(sorted);
  if (value && Object.getPrototypeOf(value) === Object.prototype) {
    return Object.fromEntries(Object.keys(value).sort().map((key) => [key, sorted(value[key])]));
  }
  return value;
}

/** A remote function's argument as one string, the same however its object keys were ordered. */
export function argKey(value) {
  return value === undefined ? "" : stringify(sorted(value));
}
