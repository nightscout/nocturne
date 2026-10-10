import type {
  BasalDeliveryOrigin,
  DeviceEventType,
  SystemEventType,
  StateSpanCategory,
  ChartSpanKind,
  TrackerCategory,
} from "$api-clients";
import type { PredictionData } from "$lib/api/prediction-data";
import { STALE_THRESHOLD_MS } from "$lib/constants/staleness";
import type { TransformedChartData } from "$lib/utils/chart-data-transform";
import { distinct } from "$lib/utils/collections";
import {
  resolveChartThresholds,
  type ChartThresholds,
} from "$lib/constants/glucose-thresholds";
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
  isOverride?: boolean;
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
  eventType?: DeviceEventType;
  color: string;
  treatmentId?: string;
  [key: string]: unknown;
}

/** A system event marker from the chart data */
export interface SystemEventMarkerData {
  time: Date;
  id?: string;
  eventType?: SystemEventType;
  color: string;
  [key: string]: unknown;
}

/** A basal injection marker from the chart data */
export interface BasalInjectionMarkerData {
  time: Date;
  id: string;
  units: number;
  insulinName?: string | null;
}

/** A BG check (fingerprick) marker from the chart data */
export interface BgCheckMarkerData {
  time: Date;
  glucose: number;
  glucoseType?: string | null;
  treatmentId?: string | null;
}

/** A tracker expiration marker */
export interface TrackerMarkerData {
  time: Date;
  id?: string;
  category?: TrackerCategory;
  color: string;
  [key: string]: unknown;
}

/**
 * A state span (pump mode, override, profile, activity, temp basal, basal
 * delivery)
 */
export interface StateSpan {
  id?: string;
  kind?: ChartSpanKind;
  category?: StateSpanCategory;
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
  origin?: (typeof BasalDeliveryOrigin)[keyof typeof BasalDeliveryOrigin];
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

const metadataString = (value: unknown): string | undefined =>
  typeof value === "string" ? value : undefined;

const metadataNumber = (value: unknown): number | undefined =>
  typeof value === "number" ? value : undefined;

/** A basal series point with its colours resolved to CSS values */
export type ChartBasalPoint = TransformedChartData["basalSeries"][number];

/** Scheduled basal point for the dotted overlay */
export interface ScheduledBasalPoint {
  timestamp?: number;
  rate?: number;
}

// ===== Proximity constant =====

export const TREATMENT_PROXIMITY_MS = 5 * 60 * 1000;

/** All lookup functions for tooltip and inspection consumers */
export interface SeriesFinders {
  findSeriesValue: <T extends { time: Date }>(
    series: T[],
    time: Date
  ) => T | undefined;
  findBasalValue: <T extends { timestamp?: number }>(
    series: T[],
    time: Date
  ) => T | undefined;
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
  /** Why the last chart-data fetch was refused, or null when it was not. */
  readonly chartDataError: string | null;
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
  readonly basalInjectionMarkers: BasalInjectionMarkerData[];
  readonly bgCheckMarkers: BgCheckMarkerData[];
  readonly iobData: SeriesPoint[];
  readonly cobData: SeriesPoint[];
  readonly basalData: ChartBasalPoint[];
  readonly scheduledBasalData: ScheduledBasalPoint[];
  readonly maxIOB: number;
  readonly maxBasalRate: number;

  // Thresholds
  readonly lowThreshold: number;
  readonly highThreshold: number;
  readonly veryHighThreshold: number;
  readonly veryLowThreshold: number;
  readonly glucoseYMax: number;
  readonly thresholds: ChartThresholds;
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

// ===== Date helpers =====
// Outside the factory: svelte/prefer-svelte-reactivity reports every Date built
// inside an exported function, and none of these is ever mutated.

function dateSpan(startMs: number, endMs: number): { start: Date; end: Date } {
  return { start: new Date(startMs), end: new Date(endMs) };
}

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

/**
 * What the fetch, realtime and prediction layers hand the pure derivations.
 * `thresholdsOverride` stands in for the server payload's thresholds when set.
 */
export type ChartDataViewSource = {
  readonly thresholdsOverride?: ChartThresholds;
} & Pick<
  ChartDataEngine,
  | "serverChartData"
  | "glucoseData"
  | "predictionData"
  | "predictionError"
  | "chartDataError"
  | "predictionServiceAvailable"
  | "effectiveShowPredictions"
  | "nowMinute"
  | "lookbackHours"
  | "fullDataRange"
  | "displayDateRange"
  | "displayDateRangeWithPredictions"
  | "fullXDomain"
>;

/**
 * Every series, span and finder a chart reads, derived from `source`. Pass
 * `source` as getters: it is read reactively, not captured. `onDataReady`
 * fires once, when `serverChartData` first becomes non-null.
 */
export function createChartDataView(
  source: ChartDataViewSource,
  onDataReady?: () => void
): ChartDataEngine {
  const serverChartData = $derived(source.serverChartData);

  let dataReadyFired = false;
  $effect(() => {
    if (serverChartData && !dataReadyFired) {
      dataReadyFired = true;
      onDataReady?.();
    }
  });
  const glucoseData = $derived(source.glucoseData);
  const fullDataRange = $derived(source.fullDataRange);
  const displayDateRange = $derived(source.displayDateRange);

  // ---- Series derivations ----
  const bolusMarkers = $derived(serverChartData?.bolusMarkers ?? []);
  const carbMarkers = $derived(serverChartData?.carbMarkers ?? []);
  const deviceEventMarkers = $derived(
    serverChartData?.deviceEventMarkers ?? []
  );
  const basalInjectionMarkers = $derived(
    serverChartData?.basalInjectionMarkers ?? []
  );
  const bgCheckMarkers = $derived(serverChartData?.bgCheckMarkers ?? []);
  const iobData = $derived(serverChartData?.iobSeries ?? []);
  const cobData = $derived(serverChartData?.cobSeries ?? []);
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
  const resolvedThresholds = $derived(
    source.thresholdsOverride ?? resolveChartThresholds(serverChartData?.thresholds)
  );
  const lowThreshold = $derived(resolvedThresholds.low);
  const highThreshold = $derived(resolvedThresholds.high);
  const veryHighThreshold = $derived(resolvedThresholds.veryHigh);
  const veryLowThreshold = $derived(resolvedThresholds.veryLow);
  const glucoseYMax = $derived(resolvedThresholds.glucoseYMax);

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

  const processedStateSpans = $derived.by(() => {
    const rangeStart = fullDataRange.from.getTime();
    const rangeEnd = fullDataRange.to.getTime();

    const pumpMode = processSpans(pumpModeSpans, rangeStart, rangeEnd);

    const override = processSpans(overrideSpans, rangeStart, rangeEnd);

    const profile = processSpans(profileSpans, rangeStart, rangeEnd).map(
      (span) => ({
        ...span,
        profileName:
          metadataString(span.metadata?.profileName) ?? span.state ?? "",
      })
    );

    const activity = processSpans(activitySpans, rangeStart, rangeEnd);

    const tempBasal = processSpans(tempBasalSpans, rangeStart, rangeEnd).map(
      (span) => ({
        ...span,
        rate:
          metadataNumber(span.metadata?.rate) ??
          metadataNumber(span.metadata?.absolute) ??
          null,
        percent: metadataNumber(span.metadata?.percent) ?? null,
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
  const displayBasalDeliverySpans = $derived(processedStateSpans.basalDelivery);
  const displaySystemEvents = $derived(processedStateSpans.events);

  // ---- Tracker markers filtered to display range ----
  const displayTrackerMarkers = $derived.by(() => {
    const rangeStart = displayDateRange.from.getTime();
    const predEnd = source.predictionData
      ? source.displayDateRangeWithPredictions.to.getTime()
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
      return dateSpan(lastBasalSourceTime, rangeEndTime);
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
      (a, b) => (b.endTime?.getTime() ?? now) - (a.endTime?.getTime() ?? now)
    );
    return sorted[0]?.state ?? "Automatic";
  });

  const uniquePumpModes = $derived(
    distinct(displayPumpModeSpans.map((s) => s.state ?? ""))
  );

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
        Math.abs(event.time.getTime() - time.getTime()) < TREATMENT_PROXIMITY_MS
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
    get serverChartData() {
      return serverChartData;
    },
    get glucoseData() {
      return glucoseData;
    },
    get predictionData() {
      return source.predictionData;
    },
    get predictionError() {
      return source.predictionError;
    },
    get chartDataError() {
      return source.chartDataError;
    },
    get predictionServiceAvailable() {
      return source.predictionServiceAvailable;
    },
    get effectiveShowPredictions() {
      return source.effectiveShowPredictions;
    },
    get nowMinute() {
      return source.nowMinute;
    },
    get lookbackHours() {
      return source.lookbackHours;
    },
    get fullDataRange() {
      return fullDataRange;
    },
    get displayDateRange() {
      return displayDateRange;
    },
    get displayDateRangeWithPredictions() {
      return source.displayDateRangeWithPredictions;
    },
    get fullXDomain() {
      return source.fullXDomain;
    },
    get bolusMarkers() {
      return bolusMarkers;
    },
    get carbMarkers() {
      return carbMarkers;
    },
    get deviceEventMarkers() {
      return deviceEventMarkers;
    },
    get basalInjectionMarkers() {
      return basalInjectionMarkers;
    },
    get bgCheckMarkers() {
      return bgCheckMarkers;
    },
    get iobData() {
      return iobData;
    },
    get cobData() {
      return cobData;
    },
    get basalData() {
      return basalData;
    },
    get scheduledBasalData() {
      return scheduledBasalData;
    },
    get maxIOB() {
      return maxIOB;
    },
    get maxBasalRate() {
      return maxBasalRate;
    },
    get lowThreshold() {
      return lowThreshold;
    },
    get highThreshold() {
      return highThreshold;
    },
    get veryHighThreshold() {
      return veryHighThreshold;
    },
    get veryLowThreshold() {
      return veryLowThreshold;
    },
    get glucoseYMax() {
      return glucoseYMax;
    },
    get thresholds() {
      return resolvedThresholds;
    },
    get medianGlucose() {
      return medianGlucose;
    },
    get displayPumpModeSpans() {
      return displayPumpModeSpans;
    },
    get displayOverrideSpans() {
      return displayOverrideSpans;
    },
    get displayProfileSpans() {
      return displayProfileSpans;
    },
    get displayActivitySpans() {
      return displayActivitySpans;
    },
    get displayTempBasalSpans() {
      return displayTempBasalSpans;
    },
    get displayBasalDeliverySpans() {
      return displayBasalDeliverySpans;
    },
    get displaySystemEvents() {
      return displaySystemEvents;
    },
    get displayTrackerMarkers() {
      return displayTrackerMarkers;
    },
    get staleBasalData() {
      return staleBasalData;
    },
    get currentPumpMode() {
      return currentPumpMode;
    },
    get uniquePumpModes() {
      return uniquePumpModes;
    },
    finders,
  };
}

/**
 * A chart over data the caller already holds, for a window it names: no
 * fetch, no realtime merge, no predictions.
 */
export function createStaticChartEngine(options: {
  readonly data: TransformedChartData;
  readonly range: { from: Date; to: Date };
  readonly onDataReady?: () => void;
}): ChartDataEngine {
  return createChartDataView({
    get serverChartData() {
      return options.data;
    },
    get glucoseData() {
      return options.data.glucoseData;
    },
    predictionData: null,
    predictionError: null,
    chartDataError: null,
    predictionServiceAvailable: false,
    effectiveShowPredictions: false,
    get nowMinute() {
      return options.range.to.getTime();
    },
    get lookbackHours() {
      return (options.range.to.getTime() - options.range.from.getTime()) / 3_600_000;
    },
    get fullDataRange() {
      return options.range;
    },
    get displayDateRange() {
      return options.range;
    },
    get displayDateRangeWithPredictions() {
      return options.range;
    },
    get fullXDomain() {
      return options.range;
    },
  }, options.onDataReady);
}
