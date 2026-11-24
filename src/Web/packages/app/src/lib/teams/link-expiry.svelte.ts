/** How long a team link works, as the days the API takes and the words for them. */
export function linkExpiryOptions(): readonly (readonly [string, string])[] {
  return [
    ["1", "1 day"],
    ["7", "7 days"],
    ["30", "30 days"],
    ["90", "90 days"],
  ];
}
