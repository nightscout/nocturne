// Lightweight glucose source for the anonymous public clock view (/clock/{id}).
//
// Unlike the authenticated RealtimeStore (which fetches many data types and holds an
// open realtime socket), this store only polls the capability-scoped, glucose-only
// endpoint keyed by the clock UUID. It exposes the same surface a clock face renders
// (ClockGlucoseSource) so ClockFaceRenderer works unchanged for logged-out viewers.

import {
  clockFaceAlertsClient,
  clockFaceDataClient,
  clockFacesClient,
} from "#lib/api/browser-clients.js";
import type {
  ClockAlertDto,
  ClockFaceConfig,
  ClockFaceData,
  ClockGlucoseDto,
} from "#lib/api/index.js";
import { toPredictionData } from "#lib/api/prediction-data.js";
import {
  resolveChartThresholds,
  transformChartData,
  type PredictionData,
  type TransformedChartData,
} from "@nocturne/charts/core";
import {
  faceDrawsChart,
  faceReadsData,
  type ClockFaceDataSource,
} from "#lib/components/clock/clock-face-data.svelte.js";
import {
  glucoseDelta,
  type ClockGlucoseSource,
} from "./realtime-store.svelte";

/** Glucose only changes every ~5 min, so a 30s poll keeps a public clock current cheaply. */
const POLL_INTERVAL_MS = 30_000;

/** An alarm should reach the room within seconds of firing. */
const ALERT_POLL_INTERVAL_MS = 10_000;

/** IOB, COB, basal and the forecast move with each reading or dose. */
const DATA_POLL_INTERVAL_MS = 60_000;

/** A chart window of hours gains one point per reading; the latest reading itself polls faster. */
const CHART_POLL_INTERVAL_MS = 5 * 60_000;

/**
 * How many polls in a row may fail before the values they last read are dropped. The values carry
 * no time of their own, so a clock that cannot reach the API would otherwise show an old dose or
 * forecast as current.
 */
const STALE_AFTER_FAILED_POLLS = 3;

export class PublicClockStore implements ClockGlucoseSource, ClockFaceDataSource {
  private readonly clockId: string;
  private pollTimer: ReturnType<typeof setInterval> | null = null;
  private alertTimer: ReturnType<typeof setInterval> | null = null;
  private dataTimer: ReturnType<typeof setInterval> | null = null;
  private chartTimer: ReturnType<typeof setInterval> | null = null;
  private failedDataPolls = 0;
  private failedChartPolls = 0;

  /** What the face's other elements show, read by the face's id; null until the first read. */
  data = $state.raw<ClockFaceData | null>(null);
  chart = $state.raw<TransformedChartData | null>(null);
  predictions = $state.raw<PredictionData | null>(null);

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

  /**
   * Poll what the face's elements show beyond the latest reading, for the elements it has. No-op
   * during SSR.
   */
  watchFace(config: ClockFaceConfig | null): void {
    if (typeof window === "undefined") return;
    this.stopFace();
    if (faceReadsData(config ?? undefined)) {
      void this.pollData();
      this.dataTimer = setInterval(() => this.pollData(), DATA_POLL_INTERVAL_MS);
    }
    if (faceDrawsChart(config ?? undefined)) {
      void this.pollChart();
      this.chartTimer = setInterval(() => this.pollChart(), CHART_POLL_INTERVAL_MS);
    }
  }

  /** Stop polling. */
  stop(): void {
    if (this.pollTimer) clearInterval(this.pollTimer);
    this.pollTimer = null;
    if (this.alertTimer) clearInterval(this.alertTimer);
    this.alertTimer = null;
    this.stopFace();
  }

  private stopFace(): void {
    if (this.dataTimer) clearInterval(this.dataTimer);
    this.dataTimer = null;
    if (this.chartTimer) clearInterval(this.chartTimer);
    this.chartTimer = null;
  }

  private async pollData(): Promise<void> {
    try {
      this.data = await clockFaceDataClient().getClockFaceData(this.clockId);
      this.failedDataPolls = 0;
    } catch (err) {
      // Keep the last values through a short outage, so one failed poll does not blank the clock.
      console.error("Failed to load public clock values:", err);
      if (++this.failedDataPolls >= STALE_AFTER_FAILED_POLLS) this.data = null;
    }
  }

  private async pollChart(): Promise<void> {
    try {
      const read = await clockFaceDataClient().getClockFaceChart(this.clockId);
      this.chart = read.chart ? transformChartData(read.chart) : null;
      this.predictions = read.predictions ? toPredictionData(read.predictions) : null;
      this.failedChartPolls = 0;
    } catch (err) {
      console.error("Failed to load public clock chart:", err);
      if (++this.failedChartPolls >= STALE_AFTER_FAILED_POLLS) {
        this.chart = null;
        this.predictions = null;
      }
    }
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
