// Lightweight glucose source for the anonymous public clock view (/clock/{id}).
//
// Unlike the authenticated RealtimeStore (which fetches many data types and holds an
// open realtime socket), this store only polls the capability-scoped, glucose-only
// endpoint keyed by the clock UUID. It exposes the same surface a clock face renders
// (ClockGlucoseSource) so ClockFaceRenderer works unchanged for logged-out viewers.

import { clockFaceAlertsClient, clockFacesClient } from "$lib/api/browser-clients";
import type { ClockAlertDto, ClockGlucoseDto } from "$lib/api";
import { resolveChartThresholds } from "$lib/constants/glucose-thresholds";
import {
  glucoseDelta,
  type ClockGlucoseSource,
} from "./realtime-store.svelte";

/** Glucose only changes every ~5 min, so a 30s poll keeps a public clock current cheaply. */
const POLL_INTERVAL_MS = 30_000;

/** An alarm should reach the room within seconds of firing. */
const ALERT_POLL_INTERVAL_MS = 10_000;

export class PublicClockStore implements ClockGlucoseSource {
  private readonly clockId: string;
  private pollTimer: ReturnType<typeof setInterval> | null = null;
  private alertTimer: ReturnType<typeof setInterval> | null = null;

  /** Alarms firing now, for a face that shows alerts; empty otherwise. */
  alerts = $state.raw<ClockAlertDto[]>([]);

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
  thresholds = resolveChartThresholds();
  demoMode = $derived(this.readings.some((r) => r.dataSource === "demo-service"));

  bgDelta = $derived(
    glucoseDelta(
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

  /** Poll the face's firing alarms while `enabled`, as its owner set it. No-op during SSR. */
  watchAlerts(enabled: boolean): void {
    if (typeof window === "undefined") return;
    if (this.alertTimer) clearInterval(this.alertTimer);
    this.alertTimer = null;
    if (!enabled) {
      this.alerts = [];
      return;
    }
    void this.pollAlerts();
    this.alertTimer = setInterval(() => this.pollAlerts(), ALERT_POLL_INTERVAL_MS);
  }

  /** Stop polling. */
  stop(): void {
    if (this.pollTimer) clearInterval(this.pollTimer);
    this.pollTimer = null;
    if (this.alertTimer) clearInterval(this.alertTimer);
    this.alertTimer = null;
  }

  /** Record that someone at the clock saw an alarm, then show the face's answer. */
  async markSeen(excursionId: string): Promise<void> {
    try {
      await clockFaceAlertsClient().markClockAlertSeen(this.clockId, excursionId);
    } catch (err) {
      // The alarm closed or the press was throttled; the poll below shows which.
      console.error("Failed to record a clock alert as seen:", err);
    }
    await this.pollAlerts();
  }

  private async pollAlerts(): Promise<void> {
    try {
      this.alerts = (await clockFaceAlertsClient().getClockAlerts(this.clockId)) ?? [];
    } catch (err) {
      // Keep showing the last answer: an alarm must not vanish on one failed poll.
      console.error("Failed to load public clock alerts:", err);
    }
  }

  private async poll(): Promise<void> {
    try {
      const data = await clockFacesClient().getGlucose(this.clockId);
      this.readings = data ?? [];
    } catch (err) {
      // A transient failure shouldn't blank the clock; keep the last good readings.
      console.error("Failed to load public clock glucose:", err);
    } finally {
      this.loaded = true;
    }
  }
}
