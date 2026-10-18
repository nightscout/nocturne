import { startOfLocalDay } from "$lib/utils/now";

/** The starting reads the document issued at parse time, keyed by request URL. */
export interface InitialReads {
  /** The clock, in epoch milliseconds, the prefetched URLs were built from. */
  now: number;
  responses: Map<string, Promise<Response>>;
}

declare global {
  interface Window {
    __nocturneInitialReads?: InitialReads;
  }
}

/** The age the prefetch is built for is the JS download wait; a wider bound only widens the gap between the reads' `to` and the socket connecting. */
const MAX_AGE_MS = 15_000;
const DAY_MS = 24 * 60 * 60 * 1000;

export function initialReadsStash(): InitialReads | undefined {
  return typeof window === "undefined" ? undefined : window.__nocturneInitialReads;
}

/** The clock the starting reads are built from: the prefetch's while it is fresh, so the URLs match. */
export function initialReadsClock(stash: InitialReads | undefined, nowMs: number): number {
  if (!stash) return nowMs;
  const age = nowMs - stash.now;
  return age >= 0 && age < MAX_AGE_MS ? stash.now : nowMs;
}

/** The ISO bounds of the starting reads. `initial-reads.prefetch.js` computes the same set. */
export function initialReadWindows(referenceMs: number): {
  oneDayAgo: string;
  to: string;
  glucoseFrom: string;
} {
  const oneDayAgoMs = referenceMs - DAY_MS;
  return {
    oneDayAgo: new Date(oneDayAgoMs).toISOString(),
    to: new Date(referenceMs).toISOString(),
    glucoseFrom: new Date(Math.min(startOfLocalDay(referenceMs), oneDayAgoMs)).toISOString(),
  };
}

/** Removes and returns the prefetched response for a GET of `url`, so it is consumed once. */
export function takeInitialRead(
  stash: InitialReads | undefined,
  url: RequestInfo,
  method: string | undefined
): Promise<Response> | undefined {
  if (!stash || typeof url !== "string") return undefined;
  if (method !== undefined && method.toUpperCase() !== "GET") return undefined;
  const response = stash.responses.get(url);
  if (response) stash.responses.delete(url);
  return response;
}

/** Cancels any prefetched response nothing took and drops the stash, so no body stays buffered. */
export function releaseInitialReads(): void {
  const stash = initialReadsStash();
  if (!stash) return;
  for (const response of stash.responses.values()) {
    response.then((r) => r.body?.cancel()).catch(() => {});
  }
  stash.responses.clear();
  delete window.__nocturneInitialReads;
}
