export function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

/** The value if it is a non-empty string, so `a || b` fallback chains over
 *  untyped payload fields can be written as `nonEmptyString(a) ?? b`. */
export function nonEmptyString(value: unknown): string | undefined {
  return typeof value === "string" && value !== "" ? value : undefined;
}
