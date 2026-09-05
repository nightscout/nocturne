// Lightweight glucose source for the anonymous public clock view (/clock/{id}).
//
// Unlike the authenticated RealtimeStore (which fetches many data types and holds an
// open realtime socket), this store only polls the capability-scoped, glucose-only
// endpoint keyed by the clock UUID. It exposes the same surface a clock face renders
// (ClockGlucoseSource) so ClockFaceRenderer works unchanged for logged-out viewers.

import { getApiClient } from "$lib/api/client";
import type { ClockGlucoseDto } from "$lib/api";
import {
  clockGlucoseDelta,
  type ClockGlucoseSource,
} from "./realtime-store.svelte";

/** Glucose only changes every ~5 min, so a 30s poll keeps a public clock current cheaply. */
const POLL_INTERVAL_MS = 30_000;

export class PublicClockStore implements ClockGlucoseSource {
  private readonly clockId: string;
  private pollTimer: ReturnType<typeof setInterval> | null = null;

  /** Most recent readings, newest first (the endpoint returns the latest two). */
  private readings = $state.raw<ClockGlucoseDto[]>([]);

  /** True once the first poll has completed (success or failure), to gate the loading UI. */
  loaded = $state(false);

  constructor(clockId: string) {
    this.clockId = clockId;
  }

  currentBG = $derived(this.readings[0]?.mgdl ?? null);
  direction = $derived(this.readings[0]?.direction ?? "");
  lastUpdated = $derived(this.readings[0]?.mills ?? null);
  demoMode = $derived(this.readings.some((r) => r.dataSource === "demo-service"));

  bgDelta = $derived(
    clockGlucoseDelta(
      this.readings[0]?.delta,
      this.readings[0]?.mgdl,
      this.readings[1]?.mgdl
    )
  );

  /** Begin polling. No-op during SSR. */
  async start(): Promise<void> {
    if (typeof window === "undefined") return;
    await this.poll();
    this.pollTimer = setInterval(() => this.poll(), POLL_INTERVAL_MS);
  }

  /** Stop polling. */
  stop(): void {
    if (this.pollTimer) clearInterval(this.pollTimer);
    this.pollTimer = null;
  }

  private async poll(): Promise<void> {
    try {
      const data = await getApiClient().clockFaces.getGlucose(this.clockId);
      this.readings = data ?? [];
    } catch (err) {
      // A transient failure shouldn't blank the clock; keep the last good readings.
      console.error("Failed to load public clock glucose:", err);
    } finally {
      this.loaded = true;
    }
  }
}
