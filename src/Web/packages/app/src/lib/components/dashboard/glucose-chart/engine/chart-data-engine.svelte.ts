import { untrack } from "svelte";
import { type BasalPoint, BasalDeliveryOrigin } from "$lib/api";
import { STALE_THRESHOLD_MS } from "$lib/constants/staleness";
import { getRealtimeStore } from "$lib/stores/realtime-store.svelte";
import { getChartData } from "$api/chart-data.remote";
import {
  getPredictions,
  getPredictionStatus,
  type PredictionData,
} from "$api/predictions.remote";
import {
  predictionMinutes,
  predictionEnabled,
  predictionDisplayMode,
  glucoseChartLookback,
  GLUCOSE_CHART_FETCH_HOURS,
} from "$lib/stores/appearance-store.svelte";
import { mergeChartData } from "$lib/utils/chart-data-merge";
import type { TransformedChartData } from "$lib/utils/chart-data-transform";
import { getGlucoseColor } from "$lib/utils/chart-colors";
import { bisector } from "d3";

// ===== Data Point Types =====

/** A single glucose reading with resolved color */
export interface GlucosePoint {
  time: Date;
  sgv: number;
  direction?: string;
  dataSource?: string;
  color: string;
}

/** A time-series point with a numeric value (IOB, COB) */
export interface SeriesPoint {
  time: Date;
  value: number;
}

/** A bolus marker from the chart data */
export interface BolusMarkerData {
  time: Date;
  insulin?: number;
  bolusType?: string;
  treatmentId?: string;
  dataSource?: string;
  [key: string]: unknown;
}

/** A carb marker from the chart data */
export interface CarbMarkerData {
  time: Date;
  carbs?: number;
  label?: string;
  treatmentId?: string;
  dataSource?: string;
  [key: string]: unknown;
}

/** A device event marker from the chart data */
export interface DeviceEventMarkerData {
  time: Date;
  eventType?: string;
  color: string;
  treatmentId?: string;
  [key: string]: unknown;
}

/** A system event marker from the chart data */
export interface SystemEventMarkerData {
  time: Date;
  id?: string;
  eventType?: string;
  color: string;
  [key: string]: unknown;
}

/** A tracker expiration marker */
export interface TrackerMarkerData {
  time: Date;
  id?: string;
  category?: string;
  color: string;
  [key: string]: unknown;
}

/** A state span (pump mode, override, profile, activity, temp basal, basal delivery) */
export interface StateSpan {
  id?: string;
  category?: string;
  state?: string;
  startTime: Date;
  endTime: Date | null;
  color: string;
  metadata?: Record<string, unknown> | null;
}

/** A state span with display-clipped start/end times */
export interface DisplaySpan extends StateSpan {
  displayStart: Date;
  displayEnd: Date;
}

/** A profile span with extracted profile name */
export interface DisplayProfileSpan extends DisplaySpan {
  profileName: string;
}

/** A temp basal span with extracted rate/percent */
export interface DisplayTempBasalSpan extends DisplaySpan {
  rate: number | null;
  percent: number | null;
}

/** Basal delivery span with rate and origin */
export interface BasalDeliverySpan {
  id?: string;
  startTime: Date;
  endTime: Date | null;
  rate?: number;
  origin?: typeof BasalDeliveryOrigin[keyof typeof BasalDeliveryOrigin];
  fillColor: string;
  strokeColor: string;
  [key: string]: unknown;
}

/** Display-clipped basal delivery span */
export interface DisplayBasalDeliverySpan extends BasalDeliverySpan {
  displayStart: Date;
  displayEnd: Date;
}

/** Stale basal time range */
export interface StaleBasalRange {
  start: Date;
  end: Date;
}

/** Scheduled basal point for the dotted overlay */
export interface ScheduledBasalPoint {
  timestamp?: number;
  rate?: number;
}

// ===== Proximity constant =====

export const TREATMENT_PROXIMITY_MS = 5 * 60 * 1000;

// ===== Options & Interfaces =====

export interface ChartDataEngineOptions {
  dateRange?: { from: Date | string; to: Date | string };
  focusHours?: number;
  initialChartData?: TransformedChartData | null;
  streamedHistoricalData?: Promise<TransformedChartData | null>;
  externalPredictionData?: PredictionData | null;
  enablePredictions?: boolean;
  demoMode?: boolean;
  /** Fired once when `serverChartData` first becomes non-null. */
  onDataReady?: () => void;
}

/** All lookup functions for tooltip and inspection consumers */
export interface SeriesFinders {
  findSeriesValue: <T extends { time: Date }>(series: T[], time: Date) => T | undefined;
  findBasalValue: <T extends { timestamp?: number }>(series: T[], time: Date) => T | undefined;
  findNearbyBolus: (time: Date) => BolusMarkerData | undefined;
  findNearbyCarbs: (time: Date) => CarbMarkerData | undefined;
  findNearbyDeviceEvent: (time: Date) => DeviceEventMarkerData | undefined;
  findActivePumpMode: (time: Date) => DisplaySpan | undefined;
  findActiveOverride: (time: Date) => DisplaySpan | undefined;
  findActiveProfile: (time: Date) => DisplayProfileSpan | undefined;
  findActiveActivities: (time: Date) => DisplaySpan[];
  findActiveTempBasal: (time: Date) => DisplayTempBasalSpan | undefined;
  findActiveBasalDelivery: (time: Date) => DisplayBasalDeliverySpan | undefined;
  findNearbySystemEvent: (time: Date) => SystemEventMarkerData | undefined;
  /** Check whether a given time falls inside the stale basal range */
  isStaleBasalTime: (time: Date) => boolean;
  /** Find the previous glucose reading before the given time */
  findPreviousGlucose: (time: Date) => GlucosePoint | undefined;
}

/** The reactive chart data engine returned by createChartDataEngine */
export interface ChartDataEngine {
  // Server / merged data
  readonly serverChartData: TransformedChartData | null;

  // Glucose (merged with realtime)
  readonly glucoseData: GlucosePoint[];

  // Predictions
  readonly predictionData: PredictionData | null;
  readonly predictionError: string | null;
  readonly predictionServiceAvailable: boolean;
  readonly effectiveShowPredictions: boolean;

  // Time ranges
  readonly nowMinute: number;
  readonly lookbackHours: number;
  readonly fullDataRange: { from: Date; to: Date };
  readonly displayDateRange: { from: Date; to: Date };
  readonly displayDateRangeWithPredictions: { from: Date; to: Date };
  readonly fullXDomain: { from: Date; to: Date };

  // Series
  readonly bolusMarkers: BolusMarkerData[];
  readonly carbMarkers: CarbMarkerData[];
  readonly deviceEventMarkers: DeviceEventMarkerData[];
  readonly iobData: SeriesPoint[];
  readonly cobData: SeriesPoint[];
  readonly basalData: BasalPoint[];
  readonly scheduledBasalData: ScheduledBasalPoint[];
  readonly maxIOB: number;
  readonly maxBasalRate: number;

  // Thresholds
  readonly lowThreshold: number;
  readonly highThreshold: number;
  readonly veryHighThreshold: number;
  readonly veryLowThreshold: number;
  readonly glucoseYMax: number;
  readonly thresholds: {
    low: number;
    high: number;
    veryLow: number;
    veryHigh: number;
    glucoseYMax: number;
  };
  readonly medianGlucose: number;

  // State spans (processed / display-clipped)
  readonly displayPumpModeSpans: DisplaySpan[];
  readonly displayOverrideSpans: DisplaySpan[];
  readonly displayProfileSpans: DisplayProfileSpan[];
  readonly displayActivitySpans: DisplaySpan[];
  readonly displayTempBasalSpans: DisplayTempBasalSpan[];
  readonly displayBasalDeliverySpans: DisplayBasalDeliverySpan[];
  readonly displaySystemEvents: SystemEventMarkerData[];

  // Tracker markers (display-filtered)
  readonly displayTrackerMarkers: TrackerMarkerData[];

  // Stale basal
  readonly staleBasalData: StaleBasalRange | null;

  // Pump mode
  readonly currentPumpMode: string;
  readonly uniquePumpModes: string[];

  // Series finders
  readonly finders: SeriesFinders;
}

// ===== Factory =====

export function createChartDataEngine(
  options: ChartDataEngineOptions
): ChartDataEngine {
  const realtimeStore = getRealtimeStore();
  const isBrowser = typeof window !== "undefined";

  // ---- Mutable state ----
  // svelte-ignore state_referenced_locally
  let serverChartData = $state<TransformedChartData | null>(
    options.initialChartData ?? null
  );
  let predictionData = $state<PredictionData | null>(null);
  let predictionError = $state<string | null>(null);
  let predictionServiceAvailable = $state(false);
  let processedHistoricalPromise =
    $state<Promise<TransformedChartData | null> | null>(null);

  // ---- Helpers ----
  function normalizeDate(
    date: Date | string | undefined,
    fallback: Date
  ): Date {
    if (!date) return fallback;
    return date instanceof Date ? date : new Date(date);
  }

  // ---- Time ranges ----
  const nowMinute = $derived(Math.floor(realtimeStore.now / 60000) * 60000);

  const lookbackHours = $derived(
    options.focusHours ?? glucoseChartLookback.current
  );

  const hasExternalPredictions = $derived(
    options.externalPredictionData !== undefined
  );

  const effectiveShowPredictions = $derived(
    (options.enablePredictions ?? true) &&
      (predictionServiceAvailable || hasExternalPredictions)
  );

  const fullDataRange = $derived({
    from: options.dateRange
      ? normalizeDate(options.dateRange.from, new Date())
      : new Date(nowMinute - GLUCOSE_CHART_FETCH_HOURS * 60 * 60 * 1000),
    to: options.dateRange
      ? normalizeDate(options.dateRange.to, new Date())
      : new Date(nowMinute),
  });

  const displayDateRange = $derived({
    from: options.dateRange
      ? normalizeDate(options.dateRange.from, new Date())
      : new Date(nowMinute - lookbackHours * 60 * 60 * 1000),
    to: options.dateRange
      ? normalizeDate(options.dateRange.to, new Date())
      : new Date(nowMinute),
  });

  const displayDateRangeWithPredictions = $derived({
    from: displayDateRange.from,
    to: effectiveShowPredictions
      ? new Date(
          displayDateRange.to.getTime() +
            predictionMinutes.current * 60 * 1000
        )
      : displayDateRange.to,
  });

  const predictionHours = $derived(predictionMinutes.current / 60);

  const fullXDomain = $derived({
    from: fullDataRange.from,
    to:
      effectiveShowPredictions && predictionData
        ? new Date(
            fullDataRange.to.getTime() + predictionHours * 60 * 60 * 1000
          )
        : fullDataRange.to,
  });

  // ---- Stable fetch range ----
  // Fetch only the visible window when no dateRange or preloaded data is
  // configured. The wider `fullDataRange` (48h) is used by the MiniOverview
  // on the dashboard, which preloads data via SSR — so consumers that hit
  // this fetch path (sidebar widget, clock face) don't need the full buffer.
  const stableFetchRange = $derived.by(() => {
    if (!isBrowser) return null;
    const range = options.dateRange ? fullDataRange : displayDateRange;
    const fromTime = range.from.getTime();
    const toTime = range.to.getTime();
    if (isNaN(fromTime) || isNaN(toTime)) return null;
    const intervalMs = 5 * 60 * 1000;
    const startRounded = Math.floor(fromTime / intervalMs) * intervalMs;
    const endRounded = Math.ceil(toTime / intervalMs) * intervalMs;
    return { startTime: startRounded, endTime: endRounded };
  });

  // ---- Effects: data fetching ----

  // Sync external prediction data when provided
  $effect(() => {
    if (hasExternalPredictions) {
      predictionData = options.externalPredictionData ?? null;
      predictionError = null;
    }
  });

  // Prediction fetch trigger (skipped when external predictions are provided)
  const predictionFetchTrigger = $derived.by(() => {
    if (!isBrowser || hasExternalPredictions) return null;
    const enabled = predictionEnabled.current;
    const latestEntryMills =
      serverChartData?.glucoseData?.[
        serverChartData.glucoseData.length - 1
      ]?.time?.getTime() ?? 0;
    if (
      !effectiveShowPredictions ||
      !enabled ||
      !serverChartData?.glucoseData?.length ||
      latestEntryMills === 0
    ) {
      return null;
    }
    return { enabled, latestEntryMills };
  });

  $effect(() => {
    const trigger = predictionFetchTrigger;
    if (!trigger) return;

    let cancelled = false;
    getPredictions({})
      .then((data) => {
        if (!cancelled) {
          predictionData = data;
          predictionError = null;
        }
      })
      .catch((err) => {
        if (!cancelled) {
          console.error("Failed to fetch predictions:", err);
          predictionError = err.message;
          predictionData = null;
        }
      });

    return () => {
      cancelled = true;
    };
  });

  // Handle streamed historical data when available
  $effect(() => {
    if (
      !options.streamedHistoricalData ||
      options.streamedHistoricalData === processedHistoricalPromise
    )
      return;

    const currentPromise = options.streamedHistoricalData;
    let cancelled = false;

    currentPromise
      .then((historicalData) => {
        if (!cancelled && historicalData && serverChartData) {
          serverChartData = mergeChartData(serverChartData, historicalData);
          processedHistoricalPromise = currentPromise;
        }
      })
      .catch((err) => {
        if (!cancelled) {
          console.error("Failed to load historical chart data:", err);
        }
      });

    return () => {
      cancelled = true;
    };
  });

  // Skip if we already have initial data from SSR streaming
  $effect(() => {
    if (options.initialChartData && untrack(() => serverChartData)) return;

    const range = stableFetchRange;
    if (!range) return;

    let cancelled = false;

    getChartData({
      startTime: range.startTime,
      endTime: range.endTime,
      intervalMinutes: 5,
    })
      .then((data) => {
        if (!cancelled) serverChartData = data;
      })
      .catch((err) => {
        if (!cancelled) {
          console.error("Failed to fetch chart data:", err);
          serverChartData = null;
        }
      });

    return () => {
      cancelled = true;
    };
  });

  // Notify caller once chart data is available (for gating playback, etc.)
  let dataReadyFired = false;
  $effect(() => {
    if (serverChartData && !dataReadyFired) {
      dataReadyFired = true;
      options.onDataReady?.();
    }
  });

  // Check prediction service availability on mount
  $effect(() => {
    if (!isBrowser) return;

    let cancelled = false;
    getPredictionStatus({})
      .then((status) => {
        if (!cancelled) {
          predictionServiceAvailable = status.available;
        }
      })
      .catch((err) => {
        if (!cancelled) {
          console.warn("Failed to check prediction service status:", err);
          predictionServiceAvailable = false;
        }
      });

    return () => {
      cancelled = true;
    };
  });

  // ---- Glucose data (merged with realtime) ----
  const glucoseData = $derived.by(() => {
    const base = serverChartData?.glucoseData ?? [];
    if (!serverChartData) return base as GlucosePoint[];

    const thresholds = serverChartData.thresholds;
    const fromMs = fullDataRange.from.getTime();
    const toMs = fullDataRange.to.getTime();
    const existingTimes = new Set(base.map((p) => p.time.getTime()));

    const realtimePoints: GlucosePoint[] = realtimeStore.entries
      .filter(
        (e) =>
          e.type === "sgv" &&
          e.mills != null &&
          e.sgv != null &&
          e.mills >= fromMs &&
          e.mills <= toMs &&
          !existingTimes.has(e.mills)
      )
      .map((e) => ({
        time: new Date(e.mills!),
        sgv: e.sgv!,
        direction: e.direction,
        dataSource: e.data_source,
        color: getGlucoseColor(e.sgv!, thresholds),
      }));

    if (realtimePoints.length === 0) return base as GlucosePoint[];

    return [...base, ...realtimePoints].sort(
      (a, b) => a.time.getTime() - b.time.getTime()
    ) as GlucosePoint[];
  });

  // ---- Series derivations ----
  const bolusMarkers = $derived(
    (serverChartData?.bolusMarkers ?? []) as BolusMarkerData[]
  );
  const carbMarkers = $derived(
    (serverChartData?.carbMarkers ?? []) as CarbMarkerData[]
  );
  const deviceEventMarkers = $derived(
    (serverChartData?.deviceEventMarkers ?? []) as DeviceEventMarkerData[]
  );
  const iobData = $derived(
    (serverChartData?.iobSeries ?? []) as SeriesPoint[]
  );
  const cobData = $derived(
    (serverChartData?.cobSeries ?? []) as SeriesPoint[]
  );
  const basalData = $derived(serverChartData?.basalSeries ?? []);
  const maxIOB = $derived(serverChartData?.maxIob ?? 3);
  const maxBasalRate = $derived(serverChartData?.maxBasalRate ?? 3.0);

  const scheduledBasalData = $derived(
    basalData.map((d) => ({
      timestamp: d.timestamp,
      rate: d.scheduledRate ?? d.rate,
    }))
  );

  // ---- Thresholds ----
  // `||` rather than `??` so a server-side 0 (no profile yet) falls back to
  // the default rather than collapsing the lines onto the X axis.
  const lowThreshold = $derived(serverChartData?.thresholds?.low || 55);
  const highThreshold = $derived(serverChartData?.thresholds?.high || 180);
  const veryHighThreshold = $derived(
    serverChartData?.thresholds?.veryHigh || 250
  );
  const veryLowThreshold = $derived(
    serverChartData?.thresholds?.veryLow || 40
  );
  const glucoseYMax = $derived(
    serverChartData?.thresholds?.glucoseYMax || 300
  );

  const medianGlucose = $derived.by(() => {
    if (glucoseData.length === 0) return 100;
    const sorted = [...glucoseData].sort((a, b) => a.sgv - b.sgv);
    const mid = Math.floor(sorted.length / 2);
    return sorted.length % 2 !== 0
      ? sorted[mid].sgv
      : (sorted[mid - 1].sgv + sorted[mid].sgv) / 2;
  });

  // ---- State spans ----
  const pumpModeSpans = $derived(serverChartData?.pumpModeSpans ?? []);
  const overrideSpans = $derived(serverChartData?.overrideSpans ?? []);
  const profileSpans = $derived(serverChartData?.profileSpans ?? []);
  const activitySpans = $derived(serverChartData?.activitySpans ?? []);
  const tempBasalSpans = $derived(serverChartData?.tempBasalSpans ?? []);
  const basalDeliverySpans = $derived(
    serverChartData?.basalDeliverySpans ?? []
  );
  const systemEvents = $derived(serverChartData?.systemEventMarkers ?? []);
  const trackerMarkers = $derived(serverChartData?.trackerMarkers ?? []);

  function processSpans<T extends { startTime: Date; endTime?: Date | null }>(
    spans: T[],
    rangeStart: number,
    rangeEnd: number
  ) {
    if (!spans) return [];
    return spans
      .filter((span) => {
        const spanStart = span.startTime.getTime();
        const spanEnd = span.endTime?.getTime() ?? rangeEnd;
        return spanEnd > rangeStart && spanStart < rangeEnd;
      })
      .map((span) => ({
        ...span,
        displayStart: new Date(Math.max(span.startTime.getTime(), rangeStart)),
        displayEnd: new Date(
          Math.min(span.endTime?.getTime() ?? rangeEnd, rangeEnd)
        ),
      }));
  }

  const processedStateSpans = $derived.by(() => {
    const rangeStart = fullDataRange.from.getTime();
    const rangeEnd = fullDataRange.to.getTime();

    const pumpMode = processSpans(pumpModeSpans, rangeStart, rangeEnd);

    const override = processSpans(overrideSpans, rangeStart, rangeEnd);

    const profile = processSpans(profileSpans, rangeStart, rangeEnd).map(
      (span) => ({
        ...span,
        profileName:
          (span.metadata?.profileName as string) ?? span.state ?? "",
      })
    );

    const activity = processSpans(activitySpans, rangeStart, rangeEnd);

    const tempBasal = processSpans(tempBasalSpans, rangeStart, rangeEnd).map(
      (span) => ({
        ...span,
        rate:
          (span.metadata?.rate as number) ??
          (span.metadata?.absolute as number) ??
          null,
        percent: (span.metadata?.percent as number) ?? null,
      })
    );

    const basalDelivery = processSpans(
      basalDeliverySpans,
      rangeStart,
      rangeEnd
    );

    const events = systemEvents.filter((event) => {
      const eventTime = event.time.getTime();
      return eventTime >= rangeStart && eventTime <= rangeEnd;
    });

    return {
      pumpMode,
      override,
      profile,
      activity,
      tempBasal,
      basalDelivery,
      events,
    };
  });

  const displayPumpModeSpans = $derived(processedStateSpans.pumpMode);
  const displayOverrideSpans = $derived(processedStateSpans.override);
  const displayProfileSpans = $derived(processedStateSpans.profile);
  const displayActivitySpans = $derived(processedStateSpans.activity);
  const displayTempBasalSpans = $derived(processedStateSpans.tempBasal);
  const displayBasalDeliverySpans = $derived(
    processedStateSpans.basalDelivery
  );
  const displaySystemEvents = $derived(processedStateSpans.events);

  // ---- Tracker markers filtered to display range ----
  const displayTrackerMarkers = $derived.by(() => {
    const rangeStart = displayDateRange.from.getTime();
    const predEnd = effectiveShowPredictions && predictionData
      ? new Date(
          displayDateRange.to.getTime() + predictionHours * 60 * 60 * 1000
        ).getTime()
      : displayDateRange.to.getTime();
    return trackerMarkers
      .filter((m) => {
        const t = m.time.getTime();
        return t >= rangeStart && t <= predEnd;
      })
      .sort((a, b) => a.time.getTime() - b.time.getTime());
  });

  // ---- Stale basal detection ----
  const lastBasalSourceTime = $derived.by(() => {
    if (displayBasalDeliverySpans.length === 0) return 0;
    let latestEndTime = 0;
    for (const span of displayBasalDeliverySpans) {
      const endTime = span.endTime?.getTime() ?? span.startTime.getTime();
      if (endTime > latestEndTime) {
        latestEndTime = endTime;
      }
    }
    return latestEndTime;
  });

  const staleBasalData = $derived.by(() => {
    if (lastBasalSourceTime === 0) return null;
    const rangeEndTime = displayDateRange.to.getTime();
    const timeSinceLastUpdate = rangeEndTime - lastBasalSourceTime;
    const rangeStartTime = displayDateRange.from.getTime();
    if (
      timeSinceLastUpdate > STALE_THRESHOLD_MS &&
      lastBasalSourceTime >= rangeStartTime
    ) {
      return {
        start: new Date(lastBasalSourceTime),
        end: new Date(rangeEndTime),
      };
    }
    return null;
  });

  // ---- Pump mode ----
  const currentPumpMode = $derived.by(() => {
    if (displayPumpModeSpans.length === 0) return "Automatic";
    const now = Date.now();
    const activeSpan = displayPumpModeSpans.find((span) => {
      const spanEnd = span.endTime?.getTime() ?? now + 1;
      return span.startTime.getTime() <= now && spanEnd >= now;
    });
    if (activeSpan) return activeSpan.state ?? "Automatic";
    const sorted = [...displayPumpModeSpans].sort(
      (a, b) =>
        (b.endTime?.getTime() ?? now) - (a.endTime?.getTime() ?? now)
    );
    return sorted[0]?.state ?? "Automatic";
  });

  const uniquePumpModes = $derived([
    ...new Set(displayPumpModeSpans.map((s) => s.state ?? "")),
  ]);

  // ---- Series finders ----
  const bisectDate = bisector((d: { time: Date }) => d.time).left;
  const bisectTimestamp = bisector(
    (d: { timestamp?: number }) => d.timestamp ?? 0
  ).left;

  function findSeriesValue<T extends { time: Date }>(
    series: T[],
    time: Date
  ): T | undefined {
    const i = bisectDate(series, time, 1);
    const d0 = series[i - 1];
    const d1 = series[i];
    if (!d0) return d1;
    if (!d1) return d0;
    return time.getTime() - d0.time.getTime() >
      d1.time.getTime() - time.getTime()
      ? d1
      : d0;
  }

  function findBasalValue<T extends { timestamp?: number }>(
    series: T[],
    time: Date
  ): T | undefined {
    if (!series || series.length === 0) return undefined;
    const timeMs = time.getTime();
    const i = bisectTimestamp(series, timeMs, 1);
    return series[i - 1];
  }

  function findNearbyBolus(time: Date) {
    return bolusMarkers.find(
      (b) =>
        Math.abs(b.time.getTime() - time.getTime()) < TREATMENT_PROXIMITY_MS
    );
  }

  function findNearbyCarbs(time: Date) {
    return carbMarkers.find(
      (c) =>
        Math.abs(c.time.getTime() - time.getTime()) < TREATMENT_PROXIMITY_MS
    );
  }

  function findNearbyDeviceEvent(time: Date) {
    return deviceEventMarkers.find(
      (d) =>
        Math.abs(d.time.getTime() - time.getTime()) < TREATMENT_PROXIMITY_MS
    );
  }

  function findActiveSpan<T extends { startTime: Date; endTime?: Date | null }>(
    spans: T[],
    time: Date,
    findAll: false
  ): T | undefined;
  function findActiveSpan<T extends { startTime: Date; endTime?: Date | null }>(
    spans: T[],
    time: Date,
    findAll: true
  ): T[];
  function findActiveSpan<T extends { startTime: Date; endTime?: Date | null }>(
    spans: T[],
    time: Date,
    findAll: boolean
  ): T | T[] | undefined {
    const timeMs = time.getTime();
    const predicate = (span: T) => {
      const spanStart = span.startTime.getTime();
      const spanEnd = span.endTime?.getTime() ?? Date.now();
      return timeMs >= spanStart && timeMs <= spanEnd;
    };
    return findAll ? spans.filter(predicate) : spans.find(predicate);
  }

  const findActivePumpMode = (time: Date) =>
    findActiveSpan(displayPumpModeSpans, time, false);
  const findActiveOverride = (time: Date) =>
    findActiveSpan(displayOverrideSpans, time, false);
  const findActiveProfile = (time: Date) =>
    findActiveSpan(displayProfileSpans, time, false);
  const findActiveActivities = (time: Date) =>
    findActiveSpan(displayActivitySpans, time, true);
  const findActiveTempBasal = (time: Date) =>
    findActiveSpan(displayTempBasalSpans, time, false);
  const findActiveBasalDelivery = (time: Date) =>
    findActiveSpan(displayBasalDeliverySpans, time, false);

  function findNearbySystemEvent(time: Date) {
    return displaySystemEvents.find(
      (event) =>
        Math.abs(event.time.getTime() - time.getTime()) <
        TREATMENT_PROXIMITY_MS
    );
  }

  function isStaleBasalTime(time: Date): boolean {
    if (!staleBasalData) return false;
    return (
      time.getTime() >= staleBasalData.start.getTime() &&
      time.getTime() <= staleBasalData.end.getTime()
    );
  }

  function findPreviousGlucose(time: Date): GlucosePoint | undefined {
    const idx = bisectDate(glucoseData, time, 1);
    return idx >= 2 ? glucoseData[idx - 2] : undefined;
  }

  // ---- Return reactive object ----
  const finders: SeriesFinders = {
    findSeriesValue,
    findBasalValue,
    findNearbyBolus,
    findNearbyCarbs,
    findNearbyDeviceEvent,
    findActivePumpMode,
    findActiveOverride,
    findActiveProfile,
    findActiveActivities,
    findActiveTempBasal,
    findActiveBasalDelivery,
    findNearbySystemEvent,
    isStaleBasalTime,
    findPreviousGlucose,
  };

  return {
    get serverChartData() { return serverChartData; },
    get glucoseData() { return glucoseData; },
    get predictionData() { return predictionData; },
    get predictionError() { return predictionError; },
    get predictionServiceAvailable() { return predictionServiceAvailable; },
    get effectiveShowPredictions() { return effectiveShowPredictions; },
    get nowMinute() { return nowMinute; },
    get lookbackHours() { return lookbackHours; },
    get fullDataRange() { return fullDataRange; },
    get displayDateRange() { return displayDateRange; },
    get displayDateRangeWithPredictions() { return displayDateRangeWithPredictions; },
    get fullXDomain() { return fullXDomain; },
    get bolusMarkers() { return bolusMarkers; },
    get carbMarkers() { return carbMarkers; },
    get deviceEventMarkers() { return deviceEventMarkers; },
    get iobData() { return iobData; },
    get cobData() { return cobData; },
    get basalData() { return basalData; },
    get scheduledBasalData() { return scheduledBasalData; },
    get maxIOB() { return maxIOB; },
    get maxBasalRate() { return maxBasalRate; },
    get lowThreshold() { return lowThreshold; },
    get highThreshold() { return highThreshold; },
    get veryHighThreshold() { return veryHighThreshold; },
    get veryLowThreshold() { return veryLowThreshold; },
    get glucoseYMax() { return glucoseYMax; },
    get thresholds() {
      return {
        low: lowThreshold,
        high: highThreshold,
        veryLow: veryLowThreshold,
        veryHigh: veryHighThreshold,
        glucoseYMax,
      };
    },
    get medianGlucose() { return medianGlucose; },
    get displayPumpModeSpans() { return displayPumpModeSpans; },
    get displayOverrideSpans() { return displayOverrideSpans; },
    get displayProfileSpans() { return displayProfileSpans; },
    get displayActivitySpans() { return displayActivitySpans; },
    get displayTempBasalSpans() { return displayTempBasalSpans; },
    get displayBasalDeliverySpans() { return displayBasalDeliverySpans; },
    get displaySystemEvents() { return displaySystemEvents; },
    get displayTrackerMarkers() { return displayTrackerMarkers; },
    get staleBasalData() { return staleBasalData; },
    get currentPumpMode() { return currentPumpMode; },
    get uniquePumpModes() { return uniquePumpModes; },
    finders,
  };
}
