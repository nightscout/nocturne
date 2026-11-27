import { plural } from "#lib/utils/plural.js";

/** How long a team link works, as the days the API takes and the words for them. */
export function linkExpiryOptions(): readonly (readonly [string, string])[] {
  return [
    ["1", plural(1, ["# day", "# days"])],
    ["7", plural(7, ["# day", "# days"])],
    ["30", plural(30, ["# day", "# days"])],
    ["90", plural(90, ["# day", "# days"])],
  ];
}
