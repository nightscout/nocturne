import { untrack } from "svelte";
import { getRealtimeStore } from "$lib/stores/realtime-store.svelte";
import { getChartData } from "$api/chart-data.remote";
import { remoteErrorMessage } from "$lib/api/remote-error";
import { PREDICTIONS_UNAVAILABLE } from "$lib/api/predictions-messages";
import {
  getPredictions,
  getPredictionStatus,
  type PredictionData,
} from "$api/predictions.remote";
import {
  predictionMinutes,
  predictionEnabled,
  glucoseChartLookback,
  GLUCOSE_CHART_FETCH_HOURS,
} from "$lib/stores/appearance-store.svelte";
import { mergeChartData } from "$lib/utils/chart-data-merge";
import type { TransformedChartData } from "$lib/utils/chart-data-transform";
import { stableBy } from "$lib/utils/stable-by";
import { mergeRealtimeGlucose } from "./merge-glucose";
import { createChartDataView, type ChartDataEngine } from "./chart-data-view.svelte";

export * from "./chart-data-view.svelte";

// ===== Options & Interfaces =====

export interface ChartDataEngineOptions {
  /**
   * Pass as a getter (`get dateRange() { … }`) wherever this can change while
   * the engine lives: read into a plain object literal it is captured once, and
   * the engine goes on fetching and drawing the window it was constructed with.
   * A consumer that instead re-creates the engine per window — `{#key}` around
   * an `{@const}` — may pass the value directly.
   */
  dateRange?: { from: Date | string; to: Date | string };
  focusHours?: number;
  initialChartData?: TransformedChartData | null;
  streamedHistoricalData?: Promise<TransformedChartData | null>;
  externalPredictionData?: PredictionData | null;
  enablePredictions?: boolean;
  demoMode?: boolean;
  /**
   * How wide a window the consumer draws, and so how far the realtime merge may
   * reach. `"buffer"` is `fullDataRange` — `dateRange` when one was named, the
   * 48-hour buffer otherwise — which is what `fullXDomain` spans and therefore
   * what anything rendering the mini overview needs, however it was fed.
   * `"display"` is the visible window alone, for a consumer that renders
   * nothing wider: the sidebar sparkline, the clock faces.
   *
   * Defaults to `"buffer"`: over-reaching costs points a chart declines to
   * draw, where under-reaching silently drops points it is drawing.
   */
  dataWindow?: "buffer" | "display";
  /** Fired once when `serverChartData` first becomes non-null. */
  onDataReady?: () => void;
}

// ===== Date helpers =====
// Outside the factory: svelte/prefer-svelte-reactivity reports every Date built
// inside an exported function, and none of these is ever mutated.

function toDate(date: Date | string | undefined): Date {
  if (!date) return new Date();
  return date instanceof Date ? date : new Date(date);
}

function hoursEndingAt(endMs: number, hours: number): { from: Date; to: Date } {
  return {
    from: new Date(endMs - hours * 60 * 60 * 1000),
    to: new Date(endMs),
  };
}

function shiftDate(date: Date, ms: number): Date {
  return new Date(date.getTime() + ms);
}

// ===== Factory =====

export function createChartDataEngine(
  options: ChartDataEngineOptions
): ChartDataEngine {
  const realtimeStore = getRealtimeStore();
  const isBrowser = typeof window !== "undefined";

  // ---- Mutable state ----
  // `$state.raw`, not `$state`: serverChartData holds large arrays of glucose
  // points, markers, and spans that are only ever reassigned wholesale (never
  // deep-mutated). Deep-proxying them makes every $derived scan register a
  // per-element dependency, turning reaction-graph reconciliation O(N^2). The
  // realtime store uses `.raw` on its arrays for the same reason.
  // svelte-ignore state_referenced_locally
  let serverChartData = $state.raw<TransformedChartData | null>(
    options.initialChartData ?? null
  );
  // `.raw` for the same reason as serverChartData: prediction series are
  // reassigned wholesale, never deep-mutated.
  let predictionData = $state.raw<PredictionData | null>(null);
  let predictionError = $state<string | null>(null);
  let chartDataError = $state<string | null>(null);
  let predictionServiceAvailable = $state(false);
  let processedHistoricalPromise =
    $state<Promise<TransformedChartData | null> | null>(null);

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

  const fullDataRange = $derived(
    options.dateRange
      ? {
          from: toDate(options.dateRange.from),
          to: toDate(options.dateRange.to),
        }
      : hoursEndingAt(nowMinute, GLUCOSE_CHART_FETCH_HOURS)
  );

  const displayDateRange = $derived(
    options.dateRange
      ? {
          from: toDate(options.dateRange.from),
          to: toDate(options.dateRange.to),
        }
      : hoursEndingAt(nowMinute, lookbackHours)
  );

  const displayDateRangeWithPredictions = $derived({
    from: displayDateRange.from,
    to: effectiveShowPredictions
      ? shiftDate(displayDateRange.to, predictionMinutes.current * 60 * 1000)
      : displayDateRange.to,
  });

  const predictionHours = $derived(predictionMinutes.current / 60);

  const fullXDomain = $derived({
    from: fullDataRange.from,
    to:
      effectiveShowPredictions && predictionData
        ? shiftDate(fullDataRange.to, predictionHours * 60 * 60 * 1000)
        : fullDataRange.to,
  });

  // ---- Data range ----
  // How far the realtime merge may reach: the window the consumer draws, per
  // `dataWindow`. The realtime store holds the last 1000 readings — several days
  // of them — so merging over `fullDataRange` for a consumer that draws only the
  // visible window hands its chart points it will never render.
  const dataRange = $derived(
    options.dataWindow === "display" ? displayDateRange : fullDataRange
  );

  // ---- Stable fetch range ----
  // Fetch only the visible window when no dateRange is configured. The wider
  // `fullDataRange` (48h) is used by the MiniOverview on the dashboard, which
  // preloads data via SSR — so consumers that hit this fetch path (sidebar
  // widget, clock face) don't need the full buffer.
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
          predictionError = remoteErrorMessage(err, PREDICTIONS_UNAVAILABLE);
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
        if (cancelled) return;
        serverChartData = data;
        chartDataError = null;
      })
      .catch((err) => {
        if (!cancelled) {
          console.error("Failed to fetch chart data:", err);
          serverChartData = null;
          chartDataError = remoteErrorMessage(err, "The chart data could not be loaded.");
        }
      });

    return () => {
      cancelled = true;
    };
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
  // Dedupe by timestamp: server data wins on collision, realtime fills gaps.
  // A keyed {#each} downstream requires a unique key per point, so the same
  // mills value must never appear twice — even if base or realtimeStore
  // emit duplicates.
  //
  // Keyed on its four inputs rather than merged on each evaluation. A derived
  // Svelte has flagged dirty stays dirty for as long as more than one batch is
  // alive — the whole time a page-level `<svelte:boundary>` is awaiting — so it
  // is re-executed on every read, not once per change. This is the most-read
  // series in the app, and a fresh array from it re-dirties the chart's entire
  // extent and scale chain, which reads it again. See the note on `stableBy`.
  const mergeGlucose = stableBy(mergeRealtimeGlucose);

  const glucoseData = $derived(
    mergeGlucose(
      serverChartData,
      realtimeStore.entries,
      dataRange.from.getTime(),
      dataRange.to.getTime()
    )
  );

  return createChartDataView({
    get serverChartData() {
      return serverChartData;
    },
    get glucoseData() {
      return glucoseData;
    },
    get predictionData() {
      return predictionData;
    },
    get predictionError() {
      return predictionError;
    },
    get chartDataError() {
      return chartDataError;
    },
    get predictionServiceAvailable() {
      return predictionServiceAvailable;
    },
    get effectiveShowPredictions() {
      return effectiveShowPredictions;
    },
    get nowMinute() {
      return nowMinute;
    },
    get lookbackHours() {
      return lookbackHours;
    },
    get fullDataRange() {
      return fullDataRange;
    },
    get displayDateRange() {
      return displayDateRange;
    },
    get displayDateRangeWithPredictions() {
      return displayDateRangeWithPredictions;
    },
    get fullXDomain() {
      return fullXDomain;
    },
  }, options.onDataReady);
}
