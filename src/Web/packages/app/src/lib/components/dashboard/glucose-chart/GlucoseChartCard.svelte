<script lang="ts">
  import { type Treatment, type BasalPoint } from "$lib/api";
  import { TreatmentEditDialog } from "$lib/components/treatments";
  import { updateTreatment } from "$lib/data/treatments.remote";
  import { toast } from "svelte-sonner";
  import {
    Card,
    CardContent,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { Badge } from "$lib/components/ui/badge";
  import { getRealtimeStore } from "$lib/stores/realtime-store.svelte";
  import {
    Chart,
    Svg,
    Axis,
    ChartClipPath,
    Highlight,
    BrushContext,
  } from "layerchart";
  import MiniOverviewChart from "../MiniOverviewChart.svelte";
  import { bisector } from "d3";
  import { scaleTime, scaleLinear } from "d3-scale";
  import {
    getPredictions,
    type PredictionData,
  } from "$lib/data/predictions.remote";
  import { getChartData } from "$lib/data/chart-data.remote";
  import {
    predictionMinutes,
    predictionEnabled,
    predictionDisplayMode,
    glucoseChartLookback,
    GLUCOSE_CHART_FETCH_HOURS,
  } from "$lib/stores/appearance-store.svelte";
  import { bg } from "$lib/utils/formatting";
  import PredictionSettings from "../PredictionSettings.svelte";

  // Sub-components
  import ZoomIndicator from "./ZoomIndicator.svelte";
  import ChartLegend from "./ChartLegend.svelte";
  import ChartTooltip from "./ChartTooltip.svelte";
  import TreatmentDisambiguationDialog from "./dialogs/TreatmentDisambiguationDialog.svelte";
  import BasalTrack from "./tracks/BasalTrack.svelte";
  import GlucoseTrack from "./tracks/GlucoseTrack.svelte";
  import IobCobTrack from "./tracks/IobCobTrack.svelte";
  import SwimLaneTrack from "./tracks/SwimLaneTrack.svelte";
  import DeviceEventMarker from "./markers/DeviceEventMarker.svelte";
  import SystemEventMarker from "./markers/SystemEventMarker.svelte";
  import TrackerExpirationMarker from "./markers/TrackerExpirationMarker.svelte";
  import { mergeChartData } from "$lib/utils/chart-data-merge";
  import type { TransformedChartData } from "$lib/utils/chart-data-transform";

  interface ComponentProps {
    demoMode?: boolean;
    dateRange?: {
      from: Date | string;
      to: Date | string;
    };
    defaultBasalRate?: number;
    carbRatio?: number;
    showPredictions?: boolean;
    defaultFocusHours?: number;
    initialShowIob?: boolean;
    initialShowCob?: boolean;
    initialShowBasal?: boolean;
    initialShowBolus?: boolean;
    initialShowCarbs?: boolean;
    initialShowDeviceEvents?: boolean;
    initialShowAlarms?: boolean;
    initialShowScheduledTrackers?: boolean;
    initialShowOverrideSpans?: boolean;
    initialShowProfileSpans?: boolean;
    initialShowActivitySpans?: boolean;
    /** Hide header, mini overview, and legend for embedded/compact mode */
    compact?: boolean;
    /** Custom height class override (e.g., "h-[300px]") */
    heightClass?: string;
    /** Initial selection domain for brush selection mode */
    selectionDomain?: [Date, Date] | null;
    /** Callback when brush selection changes (enables selection mode) */
    onSelectionChange?: (domain: [Date, Date] | null) => void;
    /** Pre-loaded initial chart data from server (SSR streaming) */
    initialChartData?: TransformedChartData | null;
    /** Promise for streamed historical data */
    streamedHistoricalData?: Promise<TransformedChartData | null>;
  }

  const realtimeStore = getRealtimeStore();
  let {
    demoMode = realtimeStore.demoMode,
    dateRange,
    defaultBasalRate = 1.0,
    carbRatio = 15,
    showPredictions = true,
    initialShowIob = true,
    initialShowCob = true,
    initialShowBasal = true,
    initialShowBolus = true,
    initialShowCarbs = true,
    initialShowDeviceEvents = true,
    initialShowAlarms = true,
    initialShowScheduledTrackers = true,
    initialShowOverrideSpans = false,
    initialShowProfileSpans = false,
    initialShowActivitySpans = false,
    compact = false,
    heightClass,
    defaultFocusHours,
    selectionDomain,
    onSelectionChange,
    initialChartData,
    streamedHistoricalData,
  }: ComponentProps = $props();

  // Selection mode is enabled when onSelectionChange callback is provided
  const isSelectionMode = $derived(!!onSelectionChange);

  // ===== STATE =====
  let predictionData = $state<PredictionData | null>(null);
  let predictionError = $state<string | null>(null);
  let serverChartData = $state<TransformedChartData | null>(
    initialChartData ?? null
  );
  // Track which promise we've already processed to allow re-processing on SPA navigation
  let processedHistoricalPromise =
    $state<Promise<TransformedChartData | null> | null>(null);

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
  let showPumpModes = $state(true);
  let expandedPumpModes = $state(false);

  // Brush/zoom state
  let brushXDomain = $state<[Date, Date] | null>(null);
  const isZoomed = $derived(brushXDomain !== null);

  function resetZoom() {
    brushXDomain = null;
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
      brushXDomain = domain;
    } else {
      brushXDomain = null;
    }
  }

  // Treatment edit dialog state
  let selectedTreatment = $state<Treatment | null>(null);
  let isTreatmentDialogOpen = $state(false);
  let isUpdatingTreatment = $state(false);
  let nearbyTreatments = $state<Treatment[]>([]);
  let isDisambiguationOpen = $state(false);

  // ===== DERIVED VALUES =====
  const isBrowser = typeof window !== "undefined";
  const nowMinute = $derived(Math.floor(realtimeStore.now / 60000) * 60000);
  const displayDemoMode = $derived(demoMode ?? realtimeStore.demoMode);
  const lookbackHours = $derived(
    defaultFocusHours ?? glucoseChartLookback.current
  );

  function normalizeDate(
    date: Date | string | undefined,
    fallback: Date
  ): Date {
    if (!date) return fallback;
    return date instanceof Date ? date : new Date(date);
  }

  // Date ranges
  const fullDataRange = $derived({
    from: dateRange
      ? normalizeDate(dateRange.from, new Date())
      : new Date(nowMinute - GLUCOSE_CHART_FETCH_HOURS * 60 * 60 * 1000),
    to: dateRange
      ? normalizeDate(dateRange.to, new Date())
      : new Date(nowMinute),
  });

  const displayDateRange = $derived({
    from: dateRange
      ? normalizeDate(dateRange.from, new Date())
      : new Date(nowMinute - lookbackHours * 60 * 60 * 1000),
    to: dateRange
      ? normalizeDate(dateRange.to, new Date())
      : new Date(nowMinute),
  });

  const displayDateRangeWithPredictions = $derived({
    from: displayDateRange.from,
    to: showPredictions
      ? new Date(
          displayDateRange.to.getTime() + predictionMinutes.current * 60 * 1000
        )
      : displayDateRange.to,
  });

  let predictionModeValue = $state(predictionDisplayMode.current);

  function handlePredictionModeChange(
    value: typeof predictionDisplayMode.current
  ) {
    if (value && value !== predictionModeValue) {
      predictionModeValue = value;
      predictionDisplayMode.current = value;
    }
  }

  // Prediction fetch
  const predictionFetchTrigger = $derived.by(() => {
    if (!isBrowser) return null;
    const enabled = predictionEnabled.current;
    const latestEntryMills =
      serverChartData?.glucoseData?.[
        serverChartData.glucoseData.length - 1
      ]?.time?.getTime() ?? 0;
    if (
      !showPredictions ||
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

  // Prediction and chart domains
  const predictionHours = $derived(predictionMinutes.current / 60);

  const fullXDomain = $derived({
    from: fullDataRange.from,
    to:
      showPredictions && predictionData
        ? new Date(
            fullDataRange.to.getTime() + predictionHours * 60 * 60 * 1000
          )
        : fullDataRange.to,
  });

  const chartXDomain = $derived({
    from: brushXDomain?.[0] ?? displayDateRange.from,
    to:
      brushXDomain?.[1] ??
      (showPredictions && predictionData
        ? new Date(
            displayDateRange.to.getTime() + predictionHours * 60 * 60 * 1000
          )
        : displayDateRange.to),
  });

  // ===== DATA FROM SERVER =====
  const glucoseData = $derived(serverChartData?.glucoseData ?? []);
  const bolusMarkers = $derived(serverChartData?.bolusMarkers ?? []);
  const carbMarkers = $derived(serverChartData?.carbMarkers ?? []);
  const deviceEventMarkers = $derived(
    serverChartData?.deviceEventMarkers ?? []
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

  // Thresholds from server (already unit-converted by remote function)
  const lowThreshold = $derived(serverChartData?.thresholds?.low ?? 55);
  const highThreshold = $derived(serverChartData?.thresholds?.high ?? 180);
  const veryHighThreshold = $derived(
    serverChartData?.thresholds?.veryHigh ?? 250
  );
  const veryLowThreshold = $derived(serverChartData?.thresholds?.veryLow ?? 40);
  const glucoseYMax = $derived(serverChartData?.thresholds?.glucoseYMax ?? 300);

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
  const SWIM_LANE_HEIGHT = 0.04;

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

  // Swim lane position types
  type SwimLanePosition = {
    top: number;
    bottom: number;
    visible: boolean;
  };

  type SwimLanePositions = {
    pumpMode: SwimLanePosition;
    override: SwimLanePosition;
    profile: SwimLanePosition;
    activity: SwimLanePosition;
  };

  let cachedSwimLaneHeight = 0;
  let cachedBasalTrackBottom = 0;
  let cachedSwimLanes: typeof trackConfig.swimLanes | null = null;
  let cachedSwimLanePositions: SwimLanePositions | null = null;

  function getSwimLanePositions(
    contextHeight: number,
    basalTrackBottom: number,
    swimLanes: typeof trackConfig.swimLanes
  ): SwimLanePositions {
    const swimLaneHeight = contextHeight * SWIM_LANE_HEIGHT;

    if (
      cachedSwimLanePositions &&
      swimLaneHeight === cachedSwimLaneHeight &&
      basalTrackBottom === cachedBasalTrackBottom &&
      cachedSwimLanes &&
      swimLanes.pumpMode === cachedSwimLanes.pumpMode &&
      swimLanes.override === cachedSwimLanes.override &&
      swimLanes.profile === cachedSwimLanes.profile &&
      swimLanes.activity === cachedSwimLanes.activity
    ) {
      return cachedSwimLanePositions;
    }

    let currentY = basalTrackBottom;
    const positions: SwimLanePositions = {
      pumpMode: { top: 0, bottom: 0, visible: false },
      override: { top: 0, bottom: 0, visible: false },
      profile: { top: 0, bottom: 0, visible: false },
      activity: { top: 0, bottom: 0, visible: false },
    };

    const laneOrder = ["pumpMode", "override", "profile", "activity"] as const;
    for (const lane of laneOrder) {
      const visible = swimLanes[lane];
      positions[lane] = {
        top: currentY,
        bottom: visible ? currentY + swimLaneHeight : currentY,
        visible,
      };
      if (visible) currentY += swimLaneHeight;
    }

    cachedSwimLaneHeight = swimLaneHeight;
    cachedBasalTrackBottom = basalTrackBottom;
    cachedSwimLanes = { ...swimLanes };
    cachedSwimLanePositions = positions;

    return positions;
  }

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
    const i = bisectTimestamp(series, { timestamp: timeMs }, 1);
    return series[i - 1];
  }

  // Treatment handling — look up treatment by ID from realtime store
  const TREATMENT_PROXIMITY_MS = 5 * 60 * 1000;

  function findTreatmentById(treatmentId: string): Treatment | undefined {
    return (realtimeStore.treatments as Treatment[]).find(
      (t) => t._id === treatmentId
    );
  }

  function findAllNearbyTreatments(time: Date): Treatment[] {
    const nearby: Treatment[] = [];

    for (const marker of bolusMarkers) {
      if (
        Math.abs(marker.time.getTime() - time.getTime()) <
        TREATMENT_PROXIMITY_MS
      ) {
        const t = findTreatmentById(marker.treatmentId ?? "");
        if (t && !nearby.some((n) => n._id === t._id)) nearby.push(t);
      }
    }

    for (const marker of carbMarkers) {
      if (
        Math.abs(marker.time.getTime() - time.getTime()) <
        TREATMENT_PROXIMITY_MS
      ) {
        const t = findTreatmentById(marker.treatmentId ?? "");
        if (t && !nearby.some((n) => n._id === t._id)) nearby.push(t);
      }
    }

    return nearby;
  }

  function handleMarkerClick(treatmentId: string) {
    const treatment = findTreatmentById(treatmentId);
    if (!treatment) return;

    const time = new Date(
      treatment.mills ??
        (treatment.created_at ? new Date(treatment.created_at).getTime() : 0)
    );
    const nearby = findAllNearbyTreatments(time);

    if (nearby.length <= 1) {
      selectedTreatment = treatment;
      isTreatmentDialogOpen = true;
    } else {
      nearbyTreatments = nearby;
      isDisambiguationOpen = true;
    }
  }

  function selectTreatmentFromList(treatment: Treatment) {
    isDisambiguationOpen = false;
    nearbyTreatments = [];
    selectedTreatment = treatment;
    isTreatmentDialogOpen = true;
  }

  async function handleTreatmentSave(updatedTreatment: Treatment) {
    isUpdatingTreatment = true;
    try {
      await updateTreatment({ ...updatedTreatment });
      toast.success("Treatment updated");
      isTreatmentDialogOpen = false;
      selectedTreatment = null;
    } catch (e) {
      console.error(e);
      toast.error("Failed to update treatment");
    } finally {
      isUpdatingTreatment = false;
    }
  }

  // Tooltip finders
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
</script>

{#snippet chartBody()}
  <Chart
    data={glucoseData}
    x={(d) => d.time}
    y="sgv"
    xScale={scaleTime()}
    xDomain={[chartXDomain.from, chartXDomain.to]}
    yDomain={[0, glucoseYMax]}
    padding={{ left: 48, bottom: 30, top: 8, right: 48 }}
    tooltip={{ mode: "quadtree-x" }}
  >
    {#snippet children({ context })}
      <Svg>
        {@const { showIobTrack, swimLanes } = trackConfig}

        {@const basalTrackHeight = context.height * trackConfig.basal}
        {@const glucoseTrackHeight = context.height * trackConfig.glucose}
        {@const iobTrackHeight = context.height * trackConfig.iob}

        {@const basalTrackTop = 0}
        {@const basalTrackBottom = basalTrackHeight}

        {@const swimLanePositions = getSwimLanePositions(
          context.height,
          basalTrackBottom,
          swimLanes
        )}

        {@const swimLanesBottom =
          basalTrackBottom + trackConfig.swimLanesRatio * context.height}
        {@const glucoseTrackTop = swimLanesBottom}
        {@const glucoseTrackBottom = glucoseTrackTop + glucoseTrackHeight}
        {@const iobTrackTop = glucoseTrackBottom}
        {@const iobTrackBottom = iobTrackTop + iobTrackHeight}

        {@const pixelToGlucoseDomain = (pixelY: number) =>
          glucoseYMax * (1 - pixelY / context.height)}

        {@const basalScale = (rate: number) => {
          const pixelY =
            basalTrackTop + (rate / maxBasalRate) * basalTrackHeight;
          return pixelToGlucoseDomain(pixelY);
        }}
        {@const basalZero = pixelToGlucoseDomain(basalTrackTop)}
        {@const basalAxisScale = scaleLinear()
          .domain([0, maxBasalRate])
          .range([basalTrackTop, basalTrackBottom])}

        {@const glucoseScale = scaleLinear()
          .domain([0, glucoseYMax])
          .range([
            pixelToGlucoseDomain(glucoseTrackBottom),
            pixelToGlucoseDomain(glucoseTrackTop),
          ])}
        {@const glucoseAxisScale = scaleLinear()
          .domain([0, glucoseYMax])
          .range([glucoseTrackBottom, glucoseTrackTop])}

        {@const iobScale = (value: number) => {
          const pixelY = iobTrackBottom - (value / maxIOB) * iobTrackHeight;
          return pixelToGlucoseDomain(pixelY);
        }}
        {@const iobZero = pixelToGlucoseDomain(iobTrackBottom)}
        {@const iobAxisScale = scaleLinear()
          .domain([0, maxIOB])
          .range([iobTrackBottom, iobTrackTop])}

        <!-- Basal Track -->
        <ChartClipPath>
          <BasalTrack
            {basalData}
            {scheduledBasalData}
            tempBasalSpans={displayTempBasalSpans}
            {staleBasalData}
            {maxBasalRate}
            {basalScale}
            {basalZero}
            {basalTrackTop}
            {basalAxisScale}
            {context}
            {showBasal}
          />
        </ChartClipPath>

        <!-- Swim Lanes -->
        <ChartClipPath>
          <SwimLaneTrack
            {context}
            {swimLanePositions}
            pumpModeSpans={displayPumpModeSpans}
            overrideSpans={displayOverrideSpans}
            profileSpans={displayProfileSpans}
            activitySpans={displayActivitySpans}
          />
        </ChartClipPath>

        <!-- Glucose Track -->
        <GlucoseTrack
          {glucoseData}
          {glucoseScale}
          {glucoseAxisScale}
          {glucoseTrackTop}
          {highThreshold}
          {lowThreshold}
          contextWidth={context.width}
          {showPredictions}
          {predictionData}
          predictionEnabled={predictionEnabled.current}
          predictionDisplayMode={predictionDisplayMode.current}
          {predictionError}
          {chartXDomain}
        />

        <!-- IOB/COB Track -->
        <IobCobTrack
          {iobData}
          {cobData}
          {carbRatio}
          {iobScale}
          {iobZero}
          {iobAxisScale}
          {iobTrackTop}
          {showIob}
          {showCob}
          {showBolus}
          {showCarbs}
          {bolusMarkers}
          {carbMarkers}
          {context}
          onMarkerClick={handleMarkerClick}
          {showIobTrack}
        />

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

          <!-- Basal highlight -->
          {#if showBasal}
            <Highlight
              x={(d) => d.time}
              y={(d) => {
                const basalDelivery = findActiveBasalDelivery(d.time);
                if (basalDelivery) {
                  return basalScale(basalDelivery.rate ?? 0);
                }
                const basal = findBasalValue(basalData, d.time);
                return basalScale(basal?.rate ?? 0);
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
      />
    {/snippet}
  </Chart>
{/snippet}

{#if compact}
  <!-- Compact mode: no card wrapper, just the chart -->
  <div class="h-full w-full @container">
    <div class="h-full">
      {@render chartBody()}
    </div>
  </div>
{:else}
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
            {showPredictions}
            predictionMode={predictionModeValue}
            onPredictionModeChange={handlePredictionModeChange}
          />
        </div>
      </div>
    </CardHeader>

    <CardContent class="p-1 @md:p-2">
      <ZoomIndicator {isZoomed} {brushXDomain} onResetZoom={resetZoom} />

      <!-- Main Chart -->
      <div class={heightClass ?? "h-80 @md:h-[450px]"}>
        {@render chartBody()}
      </div>

      <!-- Mini Overview Chart -->
      {#if glucoseData.length > 0}
        {@const miniPredictionData =
          showPredictions && predictionData?.curves?.main
            ? predictionData.curves.main.map((p) => ({
                time: new Date(p.timestamp),
                value: Number(bg(p.value)),
              }))
            : null}
        {@const miniSelectedDomain: [Date, Date] = brushXDomain ?? [
          displayDateRangeWithPredictions.from,
          displayDateRangeWithPredictions.to,
        ]}
        <MiniOverviewChart
          data={glucoseData}
          fullXDomain={[fullXDomain.from, fullXDomain.to]}
          selectedXDomain={miniSelectedDomain}
          yDomain={[0, glucoseYMax]}
          expanded={true}
          highThreshold={Number(highThreshold)}
          lowThreshold={Number(lowThreshold)}
          onSelectionChange={(domain) => handleMiniChartBrush(domain)}
          predictionData={miniPredictionData}
          showPredictions={showPredictions && predictionEnabled.current}
        />
      {/if}

      <!-- Legend -->
      <ChartLegend
        {glucoseData}
        {highThreshold}
        {lowThreshold}
        {veryHighThreshold}
        {veryLowThreshold}
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
        onToggleBasal={() => (showBasal = !showBasal)}
        onToggleIob={() => (showIob = !showIob)}
        onToggleCob={() => (showCob = !showCob)}
        onToggleBolus={() => (showBolus = !showBolus)}
        onToggleCarbs={() => (showCarbs = !showCarbs)}
        onTogglePumpModes={() => {
          showPumpModes = !showPumpModes;
          if (!showPumpModes) expandedPumpModes = false;
        }}
        onToggleAlarms={() => (showAlarms = !showAlarms)}
        onToggleScheduledTrackers={() =>
          (showScheduledTrackers = !showScheduledTrackers)}
        onToggleOverrideSpans={() => (showOverrideSpans = !showOverrideSpans)}
        onToggleProfileSpans={() => (showProfileSpans = !showProfileSpans)}
        onToggleActivitySpans={() => (showActivitySpans = !showActivitySpans)}
        {deviceEventMarkers}
        systemEvents={displaySystemEvents}
        pumpModeSpans={displayPumpModeSpans}
        scheduledTrackerMarkers={displayTrackerMarkers}
        {currentPumpMode}
        {uniquePumpModes}
        {expandedPumpModes}
        onToggleExpandedPumpModes={() =>
          (expandedPumpModes = !expandedPumpModes)}
      />
    </CardContent>
  </Card>
{/if}

<!-- Treatment Edit Dialog -->
<TreatmentEditDialog
  bind:open={isTreatmentDialogOpen}
  treatment={selectedTreatment}
  isLoading={isUpdatingTreatment}
  onClose={() => {
    isTreatmentDialogOpen = false;
    selectedTreatment = null;
  }}
  onSave={handleTreatmentSave}
/>

<!-- Disambiguation Dialog -->
<TreatmentDisambiguationDialog
  bind:open={isDisambiguationOpen}
  treatments={nearbyTreatments}
  onSelect={selectTreatmentFromList}
  onClose={() => {
    isDisambiguationOpen = false;
    nearbyTreatments = [];
  }}
/>
