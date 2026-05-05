<script lang="ts">
  import type { EntryRecord } from "$lib/constants/entry-categories";
  import {
    Card,
    CardContent,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { Badge } from "$lib/components/ui/badge";
  import { getRealtimeStore } from "$lib/stores/realtime-store.svelte";
  import {
    predictionEnabled,
    predictionDisplayMode,
    glucoseChartLookback,
    chartLineColorMode,
    chartLineColor,
    chartPointColorMode,
    chartPointColor,
    chartShowPoints,
    chartAreaMode,
    chartAreaOpacity,
  } from "$lib/stores/appearance-store.svelte";
  import type { PredictionDisplayMode } from "$lib/stores/appearance-store.svelte";
  import PredictionSettings from "../PredictionSettings.svelte";
  import MiniOverviewChart from "../MiniOverviewChart.svelte";
  import GlucoseChartShell from "./GlucoseChartShell.svelte";
  import ChartLegend from "./ChartLegend.svelte";
  import ZoomIndicator from "./ZoomIndicator.svelte";
  import { createChartDataEngine, TREATMENT_PROXIMITY_MS } from "./engine/chart-data-engine.svelte";
  import { createPointInspection } from "./engine/point-inspection.svelte";
  import { getEntryByTreatmentId } from "$api/entries.remote";
  import type { LegendState } from "./chart-context.svelte";
  import type { TransformedChartData } from "$lib/utils/chart-data-transform";
  import type { PredictionData } from "$api/predictions.remote";
  import { EntryEditDialog } from "$lib/components/entries";

  // Tracks
  import BasalTrack from "./tracks/BasalTrack.svelte";
  import SwimLaneTrack from "./tracks/SwimLaneTrack.svelte";
  import ThresholdRules from "./tracks/ThresholdRules.svelte";
  import GlucoseTrack from "./tracks/GlucoseTrack.svelte";
  import PredictionTrack from "./tracks/PredictionTrack.svelte";
  import IobCobTrack from "./tracks/IobCobTrack.svelte";
  import DeviceEventMarkers from "./markers/DeviceEventMarkers.svelte";
  import SystemEventMarkers from "./markers/SystemEventMarkers.svelte";
  import TrackerMarkers from "./markers/TrackerMarkers.svelte";
  import ChartHighlight from "./tracks/ChartHighlight.svelte";
  import ChartTooltip from "./ChartTooltip.svelte";

  // Dialogs
  import TreatmentDisambiguationDialog from "./dialogs/TreatmentDisambiguationDialog.svelte";
  import PointInspectionPicker from "./dialogs/PointInspectionPicker.svelte";
  import GlucoseInspectionDialog from "./dialogs/GlucoseInspectionDialog.svelte";
  import DeliveryInspectionDialog from "./dialogs/DeliveryInspectionDialog.svelte";
  import TreatmentInspectionDialog from "./dialogs/TreatmentInspectionDialog.svelte";
  import BasalInjectionMarker from "./markers/BasalInjectionMarker.svelte";
  import { getAll as getBasalInjections } from "$lib/api/generated/basalInjections.generated.remote";
  import type { BasalInjection } from "$api";

  interface Props {
    dateRange?: { from: Date | string; to: Date | string };
    initialChartData?: TransformedChartData | null;
    streamedHistoricalData?: Promise<TransformedChartData | null>;
    externalPredictionData?: PredictionData | null;
    showPredictions?: boolean;
    defaultFocusHours?: number;
    heightClass?: string;
    demoMode?: boolean;
  }

  let {
    dateRange,
    initialChartData,
    streamedHistoricalData,
    externalPredictionData,
    showPredictions = true,
    defaultFocusHours,
    heightClass,
    demoMode,
  }: Props = $props();

  const realtimeStore = getRealtimeStore();
  const displayDemoMode = $derived(demoMode ?? realtimeStore.demoMode);

  // ===== ENGINE =====
  // svelte-ignore state_referenced_locally
  const engine = createChartDataEngine({
    dateRange,
    focusHours: defaultFocusHours,
    initialChartData,
    streamedHistoricalData,
    externalPredictionData,
    enablePredictions: showPredictions,
    demoMode,
  });

  // ===== POINT INSPECTION =====
  // svelte-ignore state_referenced_locally
  const inspection = createPointInspection(
    engine.finders,
    () => engine.glucoseData,
    {
      iobData: () => engine.iobData,
      cobData: () => engine.cobData,
      basalData: () => engine.basalData,
    },
  );

<<<<<<< HEAD
  // ===== LEGEND STATE =====
  let showIob = $state(true);
  let showCob = $state(true);
  let showBasal = $state(true);
  let showBolus = $state(true);
  let showCarbs = $state(true);
  let showDeviceEvents = $state(true);
  let showAlarms = $state(true);
  let showScheduledTrackers = $state(true);
  let showOverrideSpans = $state(false);
  let showProfileSpans = $state(false);
  let showActivitySpans = $state(false);
=======
  // Basal injection markers (fetched separately since not in chart data DTO)
  let basalInjectionData = $state<BasalInjection[]>([]);

  // Legend toggle state
  // svelte-ignore state_referenced_locally
  let showIob = $state(initialShowIob);
  // svelte-ignore state_referenced_locally
  let showCob = $state(initialShowCob);
  // svelte-ignore state_referenced_locally
  let showBasal = $state(initialShowBasal);
  // svelte-ignore state_referenced_locally
  let showBolus = $state(initialShowBolus);
  // svelte-ignore state_referenced_locally
  let showCarbs = $state(initialShowCarbs);
  // svelte-ignore state_referenced_locally
  let showDeviceEvents = $state(initialShowDeviceEvents);
  // svelte-ignore state_referenced_locally
  let showAlarms = $state(initialShowAlarms);
  // svelte-ignore state_referenced_locally
  let showScheduledTrackers = $state(initialShowScheduledTrackers);
  // svelte-ignore state_referenced_locally
  let showOverrideSpans = $state(initialShowOverrideSpans);
  // svelte-ignore state_referenced_locally
  let showProfileSpans = $state(initialShowProfileSpans);
  // svelte-ignore state_referenced_locally
  let showActivitySpans = $state(initialShowActivitySpans);
  let showBasalInjections = $state(true);
>>>>>>> 6bfdd1b3f (feat(web): chart marker for basal injections)
  let showPumpModes = $state(true);
  let expandedPumpModes = $state(false);

  const legend: LegendState = {
    get iob() { return showIob; },
    get cob() { return showCob; },
    get basal() { return showBasal; },
    get bolus() { return showBolus; },
    get carbs() { return showCarbs; },
    get deviceEvents() { return showDeviceEvents; },
    get alarms() { return showAlarms; },
    get scheduledTrackers() { return showScheduledTrackers; },
    get overrideSpans() { return showOverrideSpans; },
    get profileSpans() { return showProfileSpans; },
    get activitySpans() { return showActivitySpans; },
    get pumpModes() { return showPumpModes; },
    get expandedPumpModes() { return expandedPumpModes; },
    toggle(key: string) {
      switch (key) {
        case "iob": showIob = !showIob; break;
        case "cob": showCob = !showCob; break;
        case "basal": showBasal = !showBasal; break;
        case "bolus": showBolus = !showBolus; break;
        case "carbs": showCarbs = !showCarbs; break;
        case "deviceEvents": showDeviceEvents = !showDeviceEvents; break;
        case "alarms": showAlarms = !showAlarms; break;
        case "scheduledTrackers": showScheduledTrackers = !showScheduledTrackers; break;
        case "overrideSpans": showOverrideSpans = !showOverrideSpans; break;
        case "profileSpans": showProfileSpans = !showProfileSpans; break;
        case "activitySpans": showActivitySpans = !showActivitySpans; break;
        case "pumpModes":
          showPumpModes = !showPumpModes;
          if (!showPumpModes) expandedPumpModes = false;
          break;
      }
    },
  };

  // ===== BRUSH / ZOOM =====
  let brushDomain = $state<[Date, Date] | null>(null);
  const isZoomed = $derived(brushDomain !== null);

  function resetZoom() {
    brushDomain = null;
  }

  function handleMiniChartBrush(domain: [Date, Date] | null) {
    if (domain) {
      const now = Date.now();
      const selectionEnd = Math.min(domain[1].getTime(), now);
      const spanMs = selectionEnd - domain[0].getTime();
      const spanHours = spanMs / (60 * 60 * 1000);
      const roundedSpan = Math.round(spanHours * 2) / 2;
      const clampedSpan = Math.max(1, Math.min(48, roundedSpan));
      glucoseChartLookback.current = clampedSpan;
      brushDomain = domain;
    } else {
      brushDomain = null;
    }
  }

  // ===== PREDICTIONS =====
  const effectiveShowPredictions = $derived(
    showPredictions && engine.effectiveShowPredictions,
  );

  let predictionModeValue = $state(predictionDisplayMode.current);

  function handlePredictionModeChange(value: PredictionDisplayMode) {
    if (value && value !== predictionModeValue) {
      predictionModeValue = value;
      predictionDisplayMode.current = value;
    }
  }

<<<<<<< HEAD
  // ===== ENTRY EDIT / MARKER CLICK =====
  let selectedEntry = $state<EntryRecord | null>(null);
  let correlatedRecords = $state<EntryRecord[]>([]);
  let isEntryDialogOpen = $state(false);
  let nearbyEntries = $state<EntryRecord[]>([]);
  let isDisambiguationOpen = $state(false);
=======
  // Sync external prediction data when provided
  $effect(() => {
    if (hasExternalPredictions) {
      predictionData = externalPredictionData ?? null;
      predictionError = null;
    }
  });

  // Prediction fetch (skipped when external predictions are provided)
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

  // Stable fetch range
  const stableFetchRange = $derived.by(() => {
    if (!isBrowser) return null;
    const fromTime = fullDataRange.from.getTime();
    const toTime = fullDataRange.to.getTime();
    if (isNaN(fromTime) || isNaN(toTime)) return null;
    const intervalMs = 5 * 60 * 1000;
    const startRounded = Math.floor(fromTime / intervalMs) * intervalMs;
    const endRounded = Math.ceil(toTime / intervalMs) * intervalMs;
    return { startTime: startRounded, endTime: endRounded };
  });

  // Handle streamed historical data when available
  $effect(() => {
    if (
      !streamedHistoricalData ||
      streamedHistoricalData === processedHistoricalPromise
    )
      return;

    const currentPromise = streamedHistoricalData;
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
    // If we have initial data from SSR, don't refetch
    if (initialChartData && serverChartData) return;

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

  // Fetch basal injections for the chart range
  $effect(() => {
    const range = stableFetchRange;
    if (!range) return;

    let cancelled = false;
    getBasalInjections({
      from: new Date(range.startTime),
      to: new Date(range.endTime),
    })
      .then((result) => {
        if (!cancelled) {
          basalInjectionData = result?.data ?? [];
        }
      })
      .catch((err) => {
        if (!cancelled) {
          console.error("Failed to fetch basal injections:", err);
          basalInjectionData = [];
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

  // Prediction and chart domains
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

  const chartXDomain = $derived({
    from: brushXDomain?.[0] ?? displayDateRange.from,
    to:
      brushXDomain?.[1] ??
      (effectiveShowPredictions && predictionData
        ? new Date(
            displayDateRange.to.getTime() + predictionHours * 60 * 60 * 1000
          )
        : displayDateRange.to),
  });

  // ===== DATA FROM SERVER =====
  // Merge realtime entries arriving via SignalR into the server-fetched chart
  // data. Without this, the chart line freezes at whatever was loaded on mount
  // even though the BG card and Recent Entries update live.
  const glucoseData = $derived.by(() => {
    const base = serverChartData?.glucoseData ?? [];
    if (!serverChartData) return base;

    const thresholds = serverChartData.thresholds;
    const fromMs = fullDataRange.from.getTime();
    const toMs = fullDataRange.to.getTime();
    const existingTimes = new Set(base.map((p) => p.time.getTime()));

    const realtimePoints = realtimeStore.entries
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

    if (realtimePoints.length === 0) return base;

    return [...base, ...realtimePoints].sort(
      (a, b) => a.time.getTime() - b.time.getTime()
    );
  });
  const bolusMarkers = $derived(serverChartData?.bolusMarkers ?? []);
  const carbMarkers = $derived(serverChartData?.carbMarkers ?? []);
  const deviceEventMarkers = $derived(
    serverChartData?.deviceEventMarkers ?? []
  );
  const basalInjectionMarkers = $derived(
    basalInjectionData.map((bi) => ({
      id: bi.id ?? "",
      time: new Date(bi.mills ?? bi.timestamp?.getTime() ?? 0),
      units: bi.units ?? 0,
      insulinName: bi.insulinContext?.insulinName,
    }))
  );
  const iobData = $derived(serverChartData?.iobSeries ?? []);
  const cobData = $derived(serverChartData?.cobSeries ?? []);
  const basalData = $derived(serverChartData?.basalSeries ?? []);
  const maxIOB = $derived(serverChartData?.maxIob ?? 3);
  const maxBasalRate = $derived(
    serverChartData?.maxBasalRate ?? defaultBasalRate * 2.5
  );

  const scheduledBasalData = $derived(
    basalData.map((d) => ({
      timestamp: d.timestamp,
      rate: d.scheduledRate ?? d.rate,
    }))
  );

  // Thresholds from server (already unit-converted by remote function). `||`
  // rather than `??` so a server-side 0 (no profile yet at the requested
  // instant) falls back to the default rather than collapsing the lines onto
  // the X axis.
  const lowThreshold = $derived(serverChartData?.thresholds?.low || 55);
  const highThreshold = $derived(serverChartData?.thresholds?.high || 180);
  const veryHighThreshold = $derived(
    serverChartData?.thresholds?.veryHigh || 250
  );
  const veryLowThreshold = $derived(serverChartData?.thresholds?.veryLow || 40);
  const glucoseYMax = $derived(serverChartData?.thresholds?.glucoseYMax || 300);

  const medianGlucose = $derived.by(() => {
    if (glucoseData.length === 0) return 100;
    const sorted = [...glucoseData].sort((a, b) => a.sgv - b.sgv);
    const mid = Math.floor(sorted.length / 2);
    return sorted.length % 2 !== 0
      ? sorted[mid].sgv
      : (sorted[mid - 1].sgv + sorted[mid].sgv) / 2;
  });

  // State spans — pre-processed by server with colors resolved
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

  // Helper function for filtering and mapping spans for display range
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

  // Batched state span processing
  const processedStateSpans = $derived.by(() => {
    const rangeStart = fullDataRange.from.getTime();
    const rangeEnd = fullDataRange.to.getTime();

    const pumpMode = processSpans(pumpModeSpans, rangeStart, rangeEnd);

    const override = processSpans(overrideSpans, rangeStart, rangeEnd);

    const profile = processSpans(profileSpans, rangeStart, rangeEnd).map(
      (span) => ({
        ...span,
        profileName: (span.metadata?.profileName as string) ?? span.state,
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

  // Derived references to processed state spans
  const displayPumpModeSpans = $derived(processedStateSpans.pumpMode);
  const displayOverrideSpans = $derived(processedStateSpans.override);
  const displayProfileSpans = $derived(processedStateSpans.profile);
  const displayActivitySpans = $derived(processedStateSpans.activity);
  const displayTempBasalSpans = $derived(processedStateSpans.tempBasal);
  const displayBasalDeliverySpans = $derived(processedStateSpans.basalDelivery);
  const displaySystemEvents = $derived(processedStateSpans.events);

  // Stale basal detection
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

  const STALE_THRESHOLD_MS = 10 * 60 * 1000;

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

  const currentPumpMode = $derived.by(() => {
    if (displayPumpModeSpans.length === 0) return "Automatic";
    const now = Date.now();
    const activeSpan = displayPumpModeSpans.find((span) => {
      const spanEnd = span.endTime?.getTime() ?? now + 1;
      return span.startTime.getTime() <= now && spanEnd >= now;
    });
    if (activeSpan) return activeSpan.state;
    const sorted = [...displayPumpModeSpans].sort(
      (a, b) => (b.endTime?.getTime() ?? now) - (a.endTime?.getTime() ?? now)
    );
    return sorted[0]?.state ?? "Automatic";
  });

  const uniquePumpModes = $derived([
    ...new Set(displayPumpModeSpans.map((s) => s.state)),
  ]);

  // Tracker markers filtered to display range
  const displayTrackerMarkers = $derived.by(() => {
    const rangeStart = displayDateRange.from.getTime();
    const rangeEnd = chartXDomain.to.getTime();
    return trackerMarkers
      .filter((m) => {
        const t = m.time.getTime();
        return t >= rangeStart && t <= rangeEnd;
      })
      .sort((a, b) => a.time.getTime() - b.time.getTime());
  });

  // ===== TRACK CONFIGURATION =====
  const trackConfig = $derived.by(() => {
    const showBasalTrack = showBasal;
    const showIobTrack = showIob || showCob;

    const swimLanes = {
      pumpMode: showPumpModes && displayPumpModeSpans.length > 0,
      override: showOverrideSpans && displayOverrideSpans.length > 0,
      profile: showProfileSpans && displayProfileSpans.length > 0,
      activity: showActivitySpans && displayActivitySpans.length > 0,
    };

    const visibleSwimLaneCount =
      Object.values(swimLanes).filter(Boolean).length;
    const swimLanesRatio = visibleSwimLaneCount * SWIM_LANE_HEIGHT;

    const basalRatio = showBasalTrack ? 0.12 : 0;
    const iobRatio = showIobTrack ? 0.18 : 0;
    const glucoseRatio = 1 - basalRatio - iobRatio - swimLanesRatio;

    return {
      basal: basalRatio,
      glucose: glucoseRatio,
      iob: iobRatio,
      swimLanes,
      swimLanesRatio,
      showBasalTrack,
      showIobTrack,
    };
  });

  // ===== HELPER FUNCTIONS =====
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

  // Entry handling — look up v4 records by marker treatmentId from realtime store
  const TREATMENT_PROXIMITY_MS = 5 * 60 * 1000;
>>>>>>> 6bfdd1b3f (feat(web): chart marker for basal injections)

  function findAllNearbyEntries(time: Date): EntryRecord[] {
    const nearby: EntryRecord[] = [];
    const seen = new Set<string>();
    const allMarkers = [
      ...engine.bolusMarkers,
      ...engine.carbMarkers,
      ...engine.deviceEventMarkers,
    ];
    for (const marker of allMarkers) {
      if (
        Math.abs(marker.time.getTime() - time.getTime()) <
        TREATMENT_PROXIMITY_MS
      ) {
        const entry = realtimeStore.findEntryByTreatmentId(
          marker.treatmentId ?? "",
        );
        if (entry && entry.data.id && !seen.has(entry.data.id)) {
          seen.add(entry.data.id);
          nearby.push(entry);
        }
      }
    }
    return nearby;
  }

  async function handleMarkerClick(treatmentId: string) {
    let entry: EntryRecord | null =
      realtimeStore.findEntryByTreatmentId(treatmentId) ?? null;

    if (!entry) {
      const result = await getEntryByTreatmentId({ treatmentId });
      entry = result as EntryRecord | null;
    }

    if (!entry) {
      console.warn(
        `[GlucoseChartCard] No entry found for treatmentId: ${treatmentId}`,
      );
      return;
    }

    const time = new Date(entry.data.mills ?? 0);
    const nearby = findAllNearbyEntries(time);

    if (nearby.length <= 1) {
      selectedEntry = entry;
      correlatedRecords = realtimeStore.findCorrelatedEntries(entry);
      isEntryDialogOpen = true;
    } else {
      nearbyEntries = nearby;
      isDisambiguationOpen = true;
    }
  }

  function selectEntryFromList(entry: EntryRecord) {
    isDisambiguationOpen = false;
    nearbyEntries = [];
    selectedEntry = entry;
    correlatedRecords = realtimeStore.findCorrelatedEntries(entry);
    isEntryDialogOpen = true;
  }

  // ===== INSPECTION DIALOG STATE =====
  let isPickerOpen = $state(false);
  let isGlucoseInspectionOpen = $state(false);
  let isDeliveryInspectionOpen = $state(false);
  let isTreatmentInspectionOpen = $state(false);

  $effect(() => {
    const dialog = inspection.activeDialog;
    isPickerOpen = dialog === "picker";
    isGlucoseInspectionOpen = dialog === "glucose";
    isDeliveryInspectionOpen = dialog === "delivery";
    isTreatmentInspectionOpen = dialog === "treatment";
  });

  function handleInspectionSelect(type: "glucose" | "delivery" | "treatment") {
    inspection.selectDialog(type);
  }

  function closeAllInspections() {
    inspection.close();
  }

  // ===== MINI OVERVIEW DATA =====
  const miniPredictionData = $derived.by(() => {
    if (!effectiveShowPredictions || !engine.predictionData?.curves?.main) {
      return null;
    }
    return engine.predictionData.curves.main.map((p) => ({
      time: new Date(p.timestamp),
      value: p.value,
    }));
  });

  const miniSelectedDomain = $derived<[Date, Date]>(
    brushDomain ?? [
      engine.displayDateRangeWithPredictions.from,
      engine.displayDateRangeWithPredictions.to,
    ],
  );
</script>

<Card class="@container bg-card border-border">
  <CardHeader class="pb-2 px-3 @md:px-6">
    <div class="flex items-center justify-between flex-wrap gap-2">
      <CardTitle class="flex items-center gap-2 text-card-foreground">
        Blood Glucose
        {#if displayDemoMode}
          <Badge
            variant="outline"
            class="text-xs border-border text-muted-foreground"
          >
            Demo
          </Badge>
        {/if}
      </CardTitle>

      <div class="flex items-center gap-2">
        <PredictionSettings
          showPredictions={effectiveShowPredictions}
          predictionMode={predictionModeValue}
          onPredictionModeChange={handlePredictionModeChange}
        />
<<<<<<< HEAD
      </div>
=======

        <!-- X-Axis -->
        <Axis
          placement="bottom"
          format={"hour"}
          tickLabelProps={{ class: "text-xs fill-muted-foreground" }}
        />

        <ChartClipPath>
          <!-- Device event markers -->
          {#if showDeviceEvents}
            {#each deviceEventMarkers as marker}
              {@const xPos = context.xScale(marker.time)}
              {@const yPos = context.yScale(glucoseScale(medianGlucose))}
              <DeviceEventMarker
                {xPos}
                {yPos}
                eventType={marker.eventType}
                color={marker.color}
                treatmentId={marker.treatmentId ?? undefined}
                onMarkerClick={handleMarkerClick}
              />
            {/each}
          {/if}

          <!-- System event markers -->
          {#if showAlarms}
            {#each displaySystemEvents as event (event.id)}
              {@const xPos = context.xScale(event.time)}
              {@const yPos = context.yScale(glucoseScale(lowThreshold * 0.8))}
              <SystemEventMarker
                {xPos}
                {yPos}
                eventType={event.eventType}
                color={event.color}
              />
            {/each}
          {/if}

          <!-- Scheduled tracker expiration markers -->
          {#if showScheduledTrackers}
            {#each displayTrackerMarkers as marker (marker.id)}
              {@const xPos = context.xScale(marker.time)}
              <TrackerExpirationMarker
                {xPos}
                lineTop={basalTrackTop + 20}
                lineBottom={context.height}
                {basalTrackTop}
                time={marker.time}
                category={marker.category}
                color={marker.color}
              />
            {/each}
          {/if}

          <!-- Basal injection markers -->
          {#if showBasalInjections}
            {#each basalInjectionMarkers as marker (marker.id)}
              {@const xPos = context.xScale(marker.time)}
              <BasalInjectionMarker
                {xPos}
                lineTop={basalTrackTop + 20}
                lineBottom={context.height}
                units={marker.units}
                insulinName={marker.insulinName}
              />
            {/each}
          {/if}

          {@render annotations?.({
            xScale: context.xScale,
            yScale: context.yScale,
            width: context.width,
            height: context.height,
            padding: CHART_PADDING,
          })}

          <!-- Basal highlight -->
          {#if showBasal}
            <Highlight
              x={(d) => d.time}
              y={(d) => {
                const basal = findBasalValue(basalData, d.time);
                if (basal) {
                  return basalScale(basal.rate ?? 0);
                }
                const basalDelivery = findActiveBasalDelivery(d.time);
                return basalScale(basalDelivery?.rate ?? 0);
              }}
              points={{ class: "fill-insulin-basal" }}
            />
          {/if}
        </ChartClipPath>
      </Svg>

      <!-- Selection brush for selection mode -->
      {#if isSelectionMode}
        <BrushContext
          axis="x"
          mode="separated"
          xDomain={selectionDomain ?? [chartXDomain.from, chartXDomain.to]}
          onChange={(e: { xDomain: unknown }) => {
            if (
              e.xDomain &&
              Array.isArray(e.xDomain) &&
              e.xDomain.length === 2
            ) {
              onSelectionChange?.([
                new Date(e.xDomain[0] as number),
                new Date(e.xDomain[1] as number),
              ]);
            }
          }}
          classes={{
            range: "bg-warning/30 border border-warning/60 rounded",
            handle: "bg-warning hover:bg-warning/80 rounded-sm",
          }}
        />
      {/if}

      <ChartTooltip
        {context}
        findBasalValue={(time) =>
          findBasalValue(basalData, time) as BasalPoint | undefined}
        findIobValue={(time) => findSeriesValue(iobData, time)}
        findCobValue={(time) => findSeriesValue(cobData, time)}
        {findNearbyBolus}
        {findNearbyCarbs}
        {findNearbyDeviceEvent}
        {findActivePumpMode}
        {findActiveOverride}
        {findActiveProfile}
        {findActiveActivities}
        {findActiveTempBasal}
        {findActiveBasalDelivery}
        {findNearbySystemEvent}
        {showBolus}
        {showCarbs}
        {showDeviceEvents}
        {showIob}
        {showCob}
        {showBasal}
        {showPumpModes}
        {showOverrideSpans}
        {showProfileSpans}
        {showActivitySpans}
        {showAlarms}
        {staleBasalData}
        {tooltipExtras}
      />
    {/snippet}
  </Chart>
{/snippet}

{#if compact}
  <!-- Compact mode: no card wrapper, just the chart -->
  <div class="{heightClass ?? 'h-full'} w-full @container">
    <div class="h-full">
      {@render chartBody()}
>>>>>>> 6bfdd1b3f (feat(web): chart marker for basal injections)
    </div>
  </CardHeader>

  <CardContent class="p-1 @md:p-2">
    <ZoomIndicator {isZoomed} brushXDomain={brushDomain} onResetZoom={resetZoom} />

    <div class={heightClass ?? "h-80 @md:h-[450px]"}>
      <GlucoseChartShell
        {engine}
        {inspection}
        {legend}
        brushDomain={brushDomain}
      >
        {#snippet tracks(ctx)}
          <BasalTrack />
          <SwimLaneTrack />
          <ThresholdRules />
          <GlucoseTrack
            lineColorMode={chartLineColorMode.current}
            lineColor={chartLineColor.current}
            pointColorMode={chartPointColorMode.current}
            pointColor={chartPointColor.current}
            showPoints={chartShowPoints.current}
            areaMode={chartAreaMode.current}
            areaOpacity={chartAreaOpacity.current}
          />
          {#if effectiveShowPredictions}
            <PredictionTrack />
          {/if}
          <IobCobTrack onMarkerClick={handleMarkerClick} />
          <DeviceEventMarkers onMarkerClick={handleMarkerClick} />
          <SystemEventMarkers />
          <TrackerMarkers />
          <ChartHighlight />
        {/snippet}
        {#snippet overlays(_ctx)}
          <ChartTooltip />
        {/snippet}
      </GlucoseChartShell>
    </div>

    {#if engine.glucoseData.length > 0}
      <MiniOverviewChart
        data={engine.glucoseData}
        fullXDomain={[engine.fullXDomain.from, engine.fullXDomain.to]}
        selectedXDomain={miniSelectedDomain}
        yDomain={[0, engine.glucoseYMax]}
        expanded={true}
        highThreshold={Number(engine.highThreshold)}
        lowThreshold={Number(engine.lowThreshold)}
        onSelectionChange={(domain) => handleMiniChartBrush(domain)}
        predictionData={miniPredictionData}
        showPredictions={effectiveShowPredictions && predictionEnabled.current}
      />
    {/if}

    <ChartLegend
      glucoseData={engine.glucoseData}
      highThreshold={engine.highThreshold}
      lowThreshold={engine.lowThreshold}
      veryHighThreshold={engine.veryHighThreshold}
      veryLowThreshold={engine.veryLowThreshold}
      {showBasal}
      {showIob}
      {showCob}
      {showBolus}
      {showCarbs}
      {showPumpModes}
      {showAlarms}
      {showScheduledTrackers}
      {showOverrideSpans}
      {showProfileSpans}
      {showActivitySpans}
      onToggleBasal={() => legend.toggle("basal")}
      onToggleIob={() => legend.toggle("iob")}
      onToggleCob={() => legend.toggle("cob")}
      onToggleBolus={() => legend.toggle("bolus")}
      onToggleCarbs={() => legend.toggle("carbs")}
      onTogglePumpModes={() => legend.toggle("pumpModes")}
      onToggleAlarms={() => legend.toggle("alarms")}
      onToggleScheduledTrackers={() => legend.toggle("scheduledTrackers")}
      onToggleOverrideSpans={() => legend.toggle("overrideSpans")}
      onToggleProfileSpans={() => legend.toggle("profileSpans")}
      onToggleActivitySpans={() => legend.toggle("activitySpans")}
      deviceEventMarkers={engine.deviceEventMarkers}
      systemEvents={engine.displaySystemEvents}
      pumpModeSpans={engine.displayPumpModeSpans}
      scheduledTrackerMarkers={engine.displayTrackerMarkers}
      currentPumpMode={engine.currentPumpMode}
      uniquePumpModes={engine.uniquePumpModes}
      {expandedPumpModes}
      onToggleExpandedPumpModes={() => (expandedPumpModes = !expandedPumpModes)}
    />
  </CardContent>
</Card>

<!-- Entry Edit Dialog -->
<EntryEditDialog
  bind:open={isEntryDialogOpen}
  entry={selectedEntry}
  {correlatedRecords}
  onClose={() => {
    isEntryDialogOpen = false;
    selectedEntry = null;
    correlatedRecords = [];
  }}
/>

<!-- Disambiguation Dialog -->
<TreatmentDisambiguationDialog
  bind:open={isDisambiguationOpen}
  entries={nearbyEntries}
  onSelect={selectEntryFromList}
  onClose={() => {
    isDisambiguationOpen = false;
    nearbyEntries = [];
  }}
/>

<!-- Point Inspection Dialogs -->
<PointInspectionPicker
  bind:open={isPickerOpen}
  options={inspection.pickerOptions}
  onSelect={handleInspectionSelect}
  onClose={closeAllInspections}
/>

{#if inspection.timestamp && inspection.glucosePoint && inspection.context}
  <GlucoseInspectionDialog
    bind:open={isGlucoseInspectionOpen}
    timestamp={inspection.timestamp}
    glucoseValue={inspection.glucosePoint.sgv}
    glucoseColor={inspection.glucosePoint.color}
    previousGlucoseValue={inspection.context.previousGlucoseValue}
    dataSource={inspection.context.dataSource}
    glucoseData={engine.glucoseData}
    highThreshold={engine.highThreshold}
    lowThreshold={engine.lowThreshold}
    iob={inspection.context.iob}
    cob={inspection.context.cob}
    basalRate={inspection.context.basalRate}
    scheduledBasalRate={inspection.context.scheduledBasalRate}
    basalOrigin={inspection.context.basalOrigin}
    pumpMode={inspection.context.pumpMode}
    overrideState={inspection.context.overrideState}
    profileName={inspection.context.profileName}
    activityStates={inspection.context.activityStates}
    hasDeliveryContext={inspection.context.basalRate != null}
    hasTreatmentContext={inspection.context.nearbyBolus != null ||
      inspection.context.nearbyCarbs != null}
    onClose={closeAllInspections}
    onNavigateDelivery={() => inspection.navigateTo("delivery")}
    onNavigateTreatment={() => inspection.navigateTo("treatment")}
  />

  <DeliveryInspectionDialog
    bind:open={isDeliveryInspectionOpen}
    timestamp={inspection.timestamp}
    basalRate={inspection.context.basalRate}
    scheduledBasalRate={inspection.context.scheduledBasalRate}
    basalOrigin={inspection.context.basalOrigin}
    pumpMode={inspection.context.pumpMode}
    overrideState={inspection.context.overrideState}
    profileName={inspection.context.profileName}
    activityStates={inspection.context.activityStates}
    iob={inspection.context.iob}
    isStaleBasal={inspection.context.isStaleBasal}
    dataSource={inspection.context.dataSource}
    glucoseData={engine.glucoseData}
    highThreshold={engine.highThreshold}
    lowThreshold={engine.lowThreshold}
    hasGlucoseContext={true}
    hasTreatmentContext={inspection.context.nearbyBolus != null ||
      inspection.context.nearbyCarbs != null}
    onClose={closeAllInspections}
    onNavigateGlucose={() => inspection.navigateTo("glucose")}
    onNavigateTreatment={() => inspection.navigateTo("treatment")}
  />

  <TreatmentInspectionDialog
    bind:open={isTreatmentInspectionOpen}
    timestamp={inspection.timestamp}
    bolusInsulin={inspection.context.nearbyBolus?.insulin}
    bolusType={inspection.context.nearbyBolus?.bolusType}
    bolusDataSource={inspection.context.nearbyBolus?.dataSource}
    carbGrams={inspection.context.nearbyCarbs?.carbs}
    carbLabel={inspection.context.nearbyCarbs?.label}
    carbDataSource={inspection.context.nearbyCarbs?.dataSource}
    iob={inspection.context.iob}
    cob={inspection.context.cob}
    glucoseValue={inspection.glucosePoint.sgv}
    glucoseData={engine.glucoseData}
    highThreshold={engine.highThreshold}
    lowThreshold={engine.lowThreshold}
    hasGlucoseContext={true}
    hasDeliveryContext={inspection.context.basalRate != null}
    onClose={closeAllInspections}
    onNavigateGlucose={() => inspection.navigateTo("glucose")}
    onNavigateDelivery={() => inspection.navigateTo("delivery")}
    onEditEntry={() => {
      closeAllInspections();
      if (inspection.context?.nearbyBolus?.treatmentId) {
        handleMarkerClick(inspection.context.nearbyBolus.treatmentId);
      } else if (inspection.context?.nearbyCarbs?.treatmentId) {
        handleMarkerClick(inspection.context.nearbyCarbs.treatmentId);
      }
    }}
  />
{/if}
