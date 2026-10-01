/** The current instant as an ISO 8601 string. */
export function isoNow(): string {
  return new Date().toISOString();
}

/** From `fromMs` to now, as the ISO 8601 pair a range query takes. */
export function untilNow(fromMs: number): { from: string; to: string } {
  return { from: new Date(fromMs).toISOString(), to: isoNow() };
}
