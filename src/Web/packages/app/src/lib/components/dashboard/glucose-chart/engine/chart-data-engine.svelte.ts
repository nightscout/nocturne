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
import { resolveChartThresholds } from "$lib/constants/glucose-thresholds";
import { mergeChartData, replaceWindow } from "$lib/utils/chart-data-merge";
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
  /** Where `initialChartData`'s window starts, in ms; adopting a reload replaces from here. */
  initialWindowStart?: number;
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
  /**
   * Where glucose comes from. `"chart-data"` fetches the chart-data window and
   * merges the realtime store's readings into it. `"realtime"` never fetches: it
   * draws the store's readings, with the thresholds the store holds, and still
   * honours `initialChartData` if given.
   *
   * Defaults to `"chart-data"`.
   */
  glucoseSource?: "chart-data" | "realtime";
  /** Fired once when `serverChartData` first becomes non-null. */
  onDataReady?: () => void;
}

const FIVE_MINUTES_MS = 5 * 60 * 1000;
// Equals INITIAL_HOURS in routes/(authenticated)/+page.server.ts.
const RECENT_REFRESH_HOURS = 6;
const RECENT_REFRESH_DEBOUNCE_MS = 2000;
const RECENT_REFRESH_FALLBACK_MS = FIVE_MINUTES_MS;

function fingerprint(rows: readonly { id?: string; _id?: string }[]): string {
  const first = rows[0];
  const last = rows[rows.length - 1];
  return `${rows.length}:${first?.id ?? first?._id ?? ""}:${last?.id ?? last?._id ?? ""}`;
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

const FETCH_INTERVAL_MS = 5 * 60 * 1000;

function shiftDate(date: Date, ms: number): Date {
  return new Date(date.getTime() + ms);
}

// ===== Factory =====

export function createChartDataEngine(
  options: ChartDataEngineOptions
): ChartDataEngine {
  const realtimeStore = getRealtimeStore();
  const isBrowser = typeof window !== "undefined";
  const realtimeOnly = $derived(options.glucoseSource === "realtime");

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
  // `dataWindow`. The realtime store holds the last day's readings, at most 1000, so merging
  // over `fullDataRange` for a consumer that draws only the visible window hands
  // its chart points it will never render.
  const dataRange = $derived(
    options.dataWindow === "display" ? displayDateRange : fullDataRange
  );

  // ---- Stable fetch range ----
  // Fetch only the visible window when no dateRange is configured. The wider
  // `fullDataRange` (48h) is used by the MiniOverview on the dashboard, which
  // preloads data via SSR — so consumers that hit this fetch path (clock
  // face) don't need the full buffer.
  // Primitive deriveds, so the range object below is rebuilt only when a
  // rounded bound moves: `displayDateRange` is a fresh object every minute.
  const fetchRange = $derived(options.dateRange ? fullDataRange : displayDateRange);
  const fetchStart = $derived(
    Math.floor(fetchRange.from.getTime() / FETCH_INTERVAL_MS) * FETCH_INTERVAL_MS
  );
  const fetchEnd = $derived(
    Math.ceil(fetchRange.to.getTime() / FETCH_INTERVAL_MS) * FETCH_INTERVAL_MS
  );
  const stableFetchRange = $derived(
    !isBrowser || isNaN(fetchStart) || isNaN(fetchEnd)
      ? null
      : { startTime: fetchStart, endTime: fetchEnd }
  );

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
    if (realtimeOnly) return;
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

  // The effect above never refetches SSR data and realtime streams only glucose,
  // so IOB, COB, basal and treatment markers are refreshed here. Triggers: realtime
  // devicestatus creates (the AID cadence), legacy `treatments` socket events and app
  // writes via `treatmentRevision`, and the store's backfill; the timer covers IOB
  // decaying with no new data.
  const hasInitialData = $derived(!!options.initialChartData && !realtimeOnly);
  const dataFingerprint = $derived(
    hasInitialData
      ? [
          realtimeStore.treatmentRevision,
          fingerprint(realtimeStore.boluses),
          fingerprint(realtimeStore.carbIntakes),
          fingerprint(realtimeStore.bgChecks),
          fingerprint(realtimeStore.deviceEvents),
          fingerprint(realtimeStore.deviceStatuses),
        ].join("|")
      : null
  );

  let debounceTimer: ReturnType<typeof setTimeout> | undefined;
  let fallbackTimer: ReturnType<typeof setTimeout> | undefined;
  // A response is applied only while its sequence number is still current; adopting
  // page data, a newer refresh and teardown all bump it.
  let refreshSeq = 0;
  let seenFingerprint: string | null = null;
  let skippedWhileHidden = false;
  // svelte-ignore state_referenced_locally
  let seenInitialData = options.initialChartData;

  function recentWindow() {
    const endTime = Math.ceil(Date.now() / FIVE_MINUTES_MS) * FIVE_MINUTES_MS;
    return { startTime: endTime - RECENT_REFRESH_HOURS * 60 * 60 * 1000, endTime };
  }

  function scheduleRefresh() {
    clearTimeout(debounceTimer);
    debounceTimer = setTimeout(refreshRecent, RECENT_REFRESH_DEBOUNCE_MS);
  }

  function refreshRecent() {
    clearTimeout(fallbackTimer);
    fallbackTimer = setTimeout(refreshRecent, RECENT_REFRESH_FALLBACK_MS);

    if (document.visibilityState === "hidden") {
      skippedWhileHidden = true;
      return;
    }
    skippedWhileHidden = false;

    const seq = ++refreshSeq;
    const { startTime, endTime } = recentWindow();
    getChartData({ startTime, endTime, intervalMinutes: 5 })
      .run()
      .then((recent) => {
        if (seq !== refreshSeq) return;
        const current = untrack(() => serverChartData);
        serverChartData = current ? replaceWindow(current, recent, startTime) : recent;
      })
      .catch((err) => {
        if (seq === refreshSeq) console.error("Failed to refresh recent chart data:", err);
      });
  }

  // A page-data reload (every successful form submit runs invalidateAll) carries a
  // freshly computed window; take it rather than discard it.
  $effect(() => {
    const next = options.initialChartData;
    if (!next || next === seenInitialData) return;
    seenInitialData = next;
    refreshSeq++;
    const current = untrack(() => serverChartData);
    serverChartData = current ? replaceWindow(
          current,
          next,
          options.initialWindowStart ?? recentWindow().startTime
        ) : next;
  });

  $effect(() => {
    if (!isBrowser || dataFingerprint === null || !realtimeStore.isReady) return;
    // The store's own initial load rewrites these arrays; SSR data predates it by seconds.
    if (seenFingerprint === null) {
      seenFingerprint = dataFingerprint;
      return;
    }
    if (dataFingerprint === seenFingerprint) return;
    seenFingerprint = dataFingerprint;
    scheduleRefresh();
  });

  $effect(() => {
    if (!isBrowser || !hasInitialData) return;

    fallbackTimer = setTimeout(refreshRecent, RECENT_REFRESH_FALLBACK_MS);
    const onVisible = () => {
      if (document.visibilityState === "visible" && skippedWhileHidden) scheduleRefresh();
    };
    document.addEventListener("visibilitychange", onVisible);

    return () => {
      refreshSeq++;
      clearTimeout(debounceTimer);
      clearTimeout(fallbackTimer);
      document.removeEventListener("visibilitychange", onVisible);
    };
  });

  // Check prediction service availability on mount
  $effect(() => {
    if (!isBrowser || !(options.enablePredictions ?? true)) return;

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
  const resolveThresholds = stableBy(resolveChartThresholds);

  const realtimeThresholds = $derived(
    realtimeOnly ? resolveThresholds(realtimeStore.chartThresholds) : undefined
  );

  const glucoseData = $derived(
    mergeGlucose(
      serverChartData,
      realtimeStore.entries,
      dataRange.from.getTime(),
      dataRange.to.getTime(),
      realtimeThresholds
    )
  );

  return createChartDataView({
    get serverChartData() {
      return serverChartData;
    },
    get glucoseData() {
      return glucoseData;
    },
    get thresholdsOverride() {
      return realtimeThresholds;
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
