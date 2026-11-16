import type { DateSpan } from "./workspace.svelte.js";

/** Two local dates as the instants a run reads: from the start of the first day to the end of the last. */
export function spanRange(span: DateSpan): { from: string; to: string } {
  const [fy, fm, fd] = span.from.split("-").map(Number);
  const [ty, tm, td] = span.to.split("-").map(Number);
  return { from: new Date(fy, fm - 1, fd).toISOString(), to: new Date(ty, tm - 1, td + 1).toISOString() };
}
