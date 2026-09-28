import type { ApiClient } from "./http.ts";

const FIVE_MINUTES = 5 * 60 * 1000;

export interface SgvEntry {
  type: "sgv";
  sgv: number;
  date: number;
  dateString: string;
  direction: string;
  device: string;
}

/** A CGM reading every five minutes ending at `end`, newest first, values from `valueAt`. */
export function sgvSeries(opts: { count: number; end?: number; device?: string; valueAt?: (i: number) => number }): SgvEntry[] {
  const end = Math.floor((opts.end ?? Date.now()) / 1000) * 1000;
  return Array.from({ length: opts.count }, (_, i) => {
    const date = end - i * FIVE_MINUTES;
    return {
      type: "sgv" as const,
      sgv: Math.round(opts.valueAt?.(i) ?? 120 + 20 * Math.sin(i / 6)),
      date,
      dateString: new Date(date).toISOString(),
      direction: "Flat",
      device: opts.device ?? "e2e-uploader",
    };
  });
}

/** Uploads entries through the legacy v1 API, as an uploader (xDrip+, Loop) would. */
export async function postEntries(api: ApiClient, entries: object[]): Promise<void> {
  await api.ok("POST", "/api/v1/entries", entries);
}

export interface V1Treatment {
  eventType: string;
  created_at: string;
  insulin?: number;
  carbs?: number;
  notes?: string;
  enteredBy?: string;
}

export async function postTreatments(api: ApiClient, treatments: V1Treatment[]): Promise<void> {
  await api.ok("POST", "/api/v1/treatments", treatments);
}

export function minutesAgo(minutes: number, from = Date.now()): string {
  return new Date(from - minutes * 60_000).toISOString();
}
