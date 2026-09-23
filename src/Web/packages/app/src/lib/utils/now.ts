/** The current instant as an ISO 8601 string. */
export function isoNow(): string {
  return new Date().toISOString();
}

/** From `fromMs` to now, as the Date pair a range query takes. */
export function untilNow(fromMs: number): { from: Date; to: Date } {
  return { from: new Date(fromMs), to: new Date() };
}
