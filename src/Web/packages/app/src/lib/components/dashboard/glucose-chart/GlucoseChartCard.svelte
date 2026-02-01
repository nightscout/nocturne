<script lang="ts">
  import {
    type Entry,
    type Treatment,
    type DeviceStatus,
    type TreatmentFood,
    type TrackerInstanceDto,
    type TrackerDefinitionDto,
    type TrackerCategory,
  } from "$lib/api";

  // Extended Treatment type that may include foods (populated externally)
  type TreatmentWithFoods = Treatment & { foods?: TreatmentFood[] };
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
  import { Chart, Svg, Axis, ChartClipPath, Highlight } from "layerchart";
  import MiniOverviewChart from "../MiniOverviewChart.svelte";
  import { chartConfig, getMealNameForTime } from "$lib/constants";
  import { bisector } from "d3";
  import { scaleTime, scaleLinear } from "d3-scale";
  import {
    getPredictions,
    type PredictionData,
  } from "$lib/data/predictions.remote";
  import {
    getChartData,
    type DashboardChartData,
  } from "$lib/data/chart-data.remote";
  import {
    getChartStateData,
    type ChartStateData,
    type BasalDeliveryChartData,
  } from "$lib/data/state-spans.remote";
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

  interface ComponentProps {
    entries?: Entry[];
    treatments?: TreatmentWithFoods[];
    deviceStatuses?: DeviceStatus[];
    demoMode?: boolean;
    dateRange?: {
      from: Date | string;
      to: Date | string;
    };
    defaultBasalRate?: number;
    carbRatio?: number;
    isf?: number;
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
    trackerInstances?: TrackerInstanceDto[];
    trackerDefinitions?: TrackerDefinitionDto[];
    /** Hide header, mini overview, and legend for embedded/compact mode */
    compact?: boolean;
    /** Custom height class override (e.g., "h-[300px]") */
    heightClass?: string;
  }

  const realtimeStore = getRealtimeStore();
  let {
    entries = realtimeStore.entries,
    treatments = realtimeStore.treatments as TreatmentWithFoods[],
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
    trackerInstances = realtimeStore.trackerInstances,
    trackerDefinitions = realtimeStore.trackerDefinitions,
    compact = false,
    heightClass,
    defaultFocusHours,
  }: ComponentProps = $props();

  // ===== STATE =====
  let predictionData = $state<PredictionData | null>(null);
  let predictionError = $state<string | null>(null);
  let serverChartData = $state<DashboardChartData | null>(null);
  let stateData = $state<ChartStateData | null>(null);

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
  const lookbackHours = $derived(defaultFocusHours ?? glucoseChartLookback.current);

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
    const latestEntryMills = entries[0]?.mills ?? 0;
    if (
      !showPredictions ||
      !enabled ||
      entries.length === 0 ||
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

  $effect(() => {
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

    getChartStateData({ startTime: range.startTime, endTime: range.endTime })
      .then((data) => {
        if (!cancelled) stateData = data;
      })
      .catch((err) => {
        if (!cancelled) {
          console.error("Failed to fetch state data:", err);
          stateData = null;
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

  // Filter entries and treatments with early exit for empty arrays
  const filteredEntries = $derived.by(() => {
    if (entries.length === 0) return [];
    const rangeFrom = fullDataRange.from.getTime();
    const rangeTo = fullDataRange.to.getTime();
    return entries.filter((e) => {
      const entryTime = e.mills ?? 0;
      return entryTime >= rangeFrom && entryTime <= rangeTo;
    });
  });

  const filteredTreatments = $derived.by(() => {
    if (treatments.length === 0) return [];
    const rangeFrom = fullDataRange.from.getTime();
    const rangeTo = fullDataRange.to.getTime();
    return treatments.filter((t) => {
      const treatmentTime =
        t.mills ?? (t.created_at ? new Date(t.created_at).getTime() : 0);
      return treatmentTime >= rangeFrom && treatmentTime <= rangeTo;
    });
  });

  // Device event types constant
  const DEVICE_EVENT_TYPES = [
    "Sensor Start",
    "Sensor Change",
    "Sensor Stop",
    "Site Change",
    "Insulin Change",
    "Pump Battery Change",
  ] as const;

  type DeviceEventType = (typeof DEVICE_EVENT_TYPES)[number];

  // Combined treatment categorization - single pass through filteredTreatments
  const categorizedTreatments = $derived.by(() => {
    const bolus: TreatmentWithFoods[] = [];
    const carbs: TreatmentWithFoods[] = [];
    const deviceEvents: TreatmentWithFoods[] = [];

    for (const t of filteredTreatments) {
      // Check for bolus
      if (
        t.insulin &&
        t.insulin > 0 &&
        (t.eventType?.includes("Bolus") ||
          t.eventType === "SMB" ||
          t.eventType === "Correction Bolus" ||
          t.eventType === "Meal Bolus" ||
          t.eventType === "Snack Bolus" ||
          t.eventType === "Bolus Wizard" ||
          t.eventType === "Combo Bolus")
      ) {
        bolus.push(t);
      }

      // Check for carbs
      if (t.carbs && t.carbs > 0) {
        carbs.push(t);
      }

      // Check for device events
      if (
        t.eventType &&
        DEVICE_EVENT_TYPES.includes(t.eventType as DeviceEventType)
      ) {
        deviceEvents.push(t);
      }
    }

    return { bolus, carbs, deviceEvents };
  });

  // Derived references to categorized treatments
  const bolusTreatments = $derived(categorizedTreatments.bolus);
  const carbTreatments = $derived(categorizedTreatments.carbs);
  const deviceEventTreatments = $derived(categorizedTreatments.deviceEvents);

  // Glucose data
  const glucoseData = $derived(
    filteredEntries
      .filter((e) => e.sgv !== null && e.sgv !== undefined)
      .map((e) => ({
        time: new Date(e.mills ?? 0),
        sgv: Number(bg(e.sgv ?? 0)),
        color: getGlucoseColor(e.sgv ?? 0),
      }))
      .sort((a, b) => a.time.getTime() - b.time.getTime())
  );

  const medianGlucose = $derived.by(() => {
    if (glucoseData.length === 0) return 100;
    const sorted = [...glucoseData].sort((a, b) => a.sgv - b.sgv);
    const mid = Math.floor(sorted.length / 2);
    return sorted.length % 2 !== 0
      ? sorted[mid].sgv
      : (sorted[mid - 1].sgv + sorted[mid].sgv) / 2;
  });

  // Device event configuration
  const deviceEventConfig: Record<
    DeviceEventType,
    { label: string; color: string; bgColor: string }
  > = {
    "Sensor Start": {
      label: "Sensor",
      color: "var(--glucose-in-range)",
      bgColor: "hsl(var(--glucose-in-range) / 0.2)",
    },
    "Sensor Change": {
      label: "Sensor",
      color: "var(--glucose-in-range)",
      bgColor: "hsl(var(--glucose-in-range) / 0.2)",
    },
    "Sensor Stop": {
      label: "Sensor",
      color: "var(--glucose-low)",
      bgColor: "hsl(var(--glucose-low) / 0.2)",
    },
    "Site Change": {
      label: "Site",
      color: "var(--insulin-bolus)",
      bgColor: "hsl(var(--insulin-bolus) / 0.2)",
    },
    "Insulin Change": {
      label: "Reservoir",
      color: "var(--insulin-basal)",
      bgColor: "hsl(var(--insulin-basal) / 0.2)",
    },
    "Pump Battery Change": {
      label: "Battery",
      color: "var(--carbs)",
      bgColor: "hsl(var(--carbs) / 0.2)",
    },
  };

  const deviceEventMarkers = $derived(
    deviceEventTreatments.map((t) => ({
      time: new Date(getTreatmentTime(t)),
      eventType: t.eventType as DeviceEventType,
      notes: t.notes,
      config: deviceEventConfig[t.eventType as DeviceEventType],
    }))
  );

  function getTreatmentTime(t: Treatment): number {
    return t.mills ?? (t.created_at ? new Date(t.created_at).getTime() : 0);
  }

  // Thresholds
  const lowThreshold = $derived(Number(bg(chartConfig.low.threshold ?? 55)));
  const highThreshold = $derived(Number(bg(chartConfig.high.threshold ?? 180)));
  const veryHighThreshold = $derived(
    Number(bg(chartConfig.severeHigh.threshold ?? 250))
  );
  const veryLowThreshold = $derived(
    Number(bg(chartConfig.severeLow.threshold ?? 40))
  );

  const glucoseYMax = $derived.by(() => {
    const maxSgv = Math.max(...filteredEntries.map((e) => e.sgv ?? 0));
    const maxDisplayValue = bg(Math.min(400, Math.max(280, maxSgv) + 20));
    return Number(maxDisplayValue);
  });

  // Server data
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

  // Helper function for filtering and mapping spans - avoids code duplication
  function processSpans<T extends { startTime: Date; endTime?: Date | null }>(
    spans: T[] | undefined,
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

  // Batched state span processing - single computation for all span types
  // Uses fullDataRange (48h) to ensure data is available when zooming/brushing
  const processedStateSpans = $derived.by(() => {
    // Early exit if no state data available
    if (!stateData) {
      return {
        pumpMode: [],
        override: [],
        profile: [],
        activity: [],
        tempBasal: [],
        basalDelivery: [] as (BasalDeliveryChartData & { displayStart: Date; displayEnd: Date })[],
        events: [],
      };
    }

    const rangeStart = fullDataRange.from.getTime();
    const rangeEnd = fullDataRange.to.getTime();

    // Process all spans with the shared range values
    const pumpMode = processSpans(stateData.pumpModeSpans, rangeStart, rangeEnd);

    const override = processSpans(stateData.overrideSpans, rangeStart, rangeEnd);

    const profile = processSpans(stateData.profileSpans, rangeStart, rangeEnd).map(
      (span) => ({
        ...span,
        profileName: (span.metadata?.profileName as string) ?? span.state,
      })
    );

    const activity = processSpans(stateData.activitySpans, rangeStart, rangeEnd);

    const tempBasal = processSpans(stateData.tempBasalSpans, rangeStart, rangeEnd).map(
      (span) => ({
        ...span,
        rate:
          (span.metadata?.rate as number) ??
          (span.metadata?.absolute as number) ??
          null,
        percent: (span.metadata?.percent as number) ?? null,
      })
    );

    // Process basal delivery spans (all origins, with rate)
    const basalDelivery = processSpans(stateData.basalDeliverySpans, rangeStart, rangeEnd);

    const events = stateData.systemEvents
      ? stateData.systemEvents.filter((event) => {
          const eventTime = event.time.getTime();
          return eventTime >= rangeStart && eventTime <= rangeEnd;
        })
      : [];

    return { pumpMode, override, profile, activity, tempBasal, basalDelivery, events };
  });

  // Derived references to processed state spans
  const pumpModeSpans = $derived(processedStateSpans.pumpMode);
  const overrideSpans = $derived(processedStateSpans.override);
  const profileSpans = $derived(processedStateSpans.profile);
  const activitySpans = $derived(processedStateSpans.activity);
  const tempBasalSpans = $derived(processedStateSpans.tempBasal);
  const basalDeliverySpans = $derived(processedStateSpans.basalDelivery);
  const systemEvents = $derived(processedStateSpans.events);

  // Stale basal detection - use basalDeliverySpans from state data
  const lastBasalSourceTime = $derived.by(() => {
    if (basalDeliverySpans.length === 0) return 0;
    // Find the span with the latest end time
    let latestEndTime = 0;
    for (const span of basalDeliverySpans) {
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
    if (pumpModeSpans.length === 0) return "Automatic";
    const now = Date.now();
    const activeSpan = pumpModeSpans.find((span) => {
      const spanEnd = span.endTime?.getTime() ?? now + 1;
      return span.startTime.getTime() <= now && spanEnd >= now;
    });
    if (activeSpan) return activeSpan.state;
    const sorted = [...pumpModeSpans].sort(
      (a, b) => (b.endTime?.getTime() ?? now) - (a.endTime?.getTime() ?? now)
    );
    return sorted[0]?.state ?? "Automatic";
  });

  const uniquePumpModes = $derived([
    ...new Set(pumpModeSpans.map((s) => s.state)),
  ]);

  // Scheduled tracker markers
  interface ScheduledTrackerMarker {
    id: string;
    definitionId: string;
    name: string;
    category: TrackerCategory;
    time: Date;
    icon?: string;
    color: string;
  }

  function getTrackerColor(category: TrackerCategory): string {
    const colorMap: Record<TrackerCategory, string> = {
      Sensor: "var(--glucose-in-range)",
      Cannula: "var(--insulin-bolus)",
      Reservoir: "var(--insulin-basal)",
      Battery: "var(--carbs)",
      Consumable: "var(--muted-foreground)",
      Appointment: "var(--primary)",
      Reminder: "var(--primary)",
      Custom: "var(--muted-foreground)",
    };
    return colorMap[category] ?? "var(--muted-foreground)";
  }

  const scheduledTrackerMarkers = $derived.by((): ScheduledTrackerMarker[] => {
    if (!trackerInstances || trackerInstances.length === 0) return [];
    const rangeStart = displayDateRange.from.getTime();
    const rangeEnd = chartXDomain.to.getTime();

    return trackerInstances
      .filter((instance) => {
        if (!instance.expectedEndAt) return false;
        const expectedTime = new Date(instance.expectedEndAt).getTime();
        return expectedTime >= rangeStart && expectedTime <= rangeEnd;
      })
      .map((instance) => {
        const definition = trackerDefinitions?.find(
          (d) => d.id === instance.definitionId
        );
        const category =
          instance.category ??
          definition?.category ??
          ("Custom" as TrackerCategory);

        return {
          id: instance.id ?? "",
          definitionId: instance.definitionId ?? "",
          name: instance.definitionName ?? definition?.name ?? "Tracker",
          category,
          time: new Date(instance.expectedEndAt!),
          icon: definition?.icon,
          color: getTrackerColor(category),
        };
      })
      .sort((a, b) => a.time.getTime() - b.time.getTime());
  });

  function getGlucoseColor(sgv: number): string {
    const low = chartConfig.low.threshold ?? 55;
    const target = chartConfig.target.threshold ?? 80;
    const high = chartConfig.high.threshold ?? 180;
    const severeHigh = chartConfig.severeHigh.threshold ?? 250;

    if (sgv < low) return "var(--glucose-very-low)";
    if (sgv < target) return "var(--glucose-low)";
    if (sgv <= high) return "var(--glucose-in-range)";
    if (sgv <= severeHigh) return "var(--glucose-high)";
    return "var(--glucose-very-high)";
  }

  // ===== TRACK CONFIGURATION =====
  const SWIM_LANE_HEIGHT = 0.04;

  // Memoized track configuration - only recalculates when visibility toggles or span counts change
  const trackConfig = $derived.by(() => {
    const showBasalTrack = showBasal;
    const showIobTrack = showIob || showCob;

    const swimLanes = {
      pumpMode: showPumpModes && pumpModeSpans.length > 0,
      override: showOverrideSpans && overrideSpans.length > 0,
      profile: showProfileSpans && profileSpans.length > 0,
      activity: showActivitySpans && activitySpans.length > 0,
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

  // Memoized function to calculate swim lane positions (called with context.height)
  // Returns a stable reference when inputs haven't changed
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

    // Return cached result if inputs are the same
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

    // Cache the result
    cachedSwimLaneHeight = swimLaneHeight;
    cachedBasalTrackBottom = basalTrackBottom;
    cachedSwimLanes = { ...swimLanes };
    cachedSwimLanePositions = positions;

    return positions;
  }

  // ===== HELPER FUNCTIONS =====
  const bisectDate = bisector((d: { time: Date }) => d.time).left;
  const bisectTimestamp = bisector((d: { timestamp?: number }) => d.timestamp ?? 0).left;

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

  // Treatment marker data
  const bolusMarkersForIob = $derived(
    bolusTreatments.map((t) => ({
      time: new Date(getTreatmentTime(t)),
      insulin: t.insulin ?? 0,
      treatment: t,
    }))
  );

  const carbMarkersForIob = $derived.by(() => {
    const markers: Array<{
      time: Date;
      carbs: number;
      treatment: Treatment;
      label: string | null;
      isOffset?: boolean;
    }> = [];

    for (const t of carbTreatments) {
      const treatmentTime = getTreatmentTime(t);
      const foods = t.foods ?? [];
      const foodsWithOffsets = foods.filter(
        (f) => f.timeOffsetMinutes != null && f.timeOffsetMinutes !== 0
      );

      if (foodsWithOffsets.length > 0) {
        const offsetGroups = new Map<
          number,
          { carbs: number; labels: string[] }
        >();
        let baseCarbs = 0;
        const baseLabels: string[] = [];

        for (const food of foods) {
          const offset = food.timeOffsetMinutes ?? 0;
          const foodCarbs = food.carbs ?? 0;
          const foodLabel = food.foodName ?? food.note;

          if (offset === 0) {
            baseCarbs += foodCarbs;
            if (foodLabel) baseLabels.push(foodLabel);
          } else {
            const existing = offsetGroups.get(offset) ?? {
              carbs: 0,
              labels: [],
            };
            existing.carbs += foodCarbs;
            if (foodLabel) existing.labels.push(foodLabel);
            offsetGroups.set(offset, existing);
          }
        }

        const totalFoodCarbs = foods.reduce(
          (sum, f) => sum + (f.carbs ?? 0),
          0
        );
        const treatmentCarbs = t.carbs ?? 0;
        const unattributedCarbs = treatmentCarbs - totalFoodCarbs;
        if (unattributedCarbs > 0) baseCarbs += unattributedCarbs;

        if (baseCarbs > 0) {
          const baseLabel =
            baseLabels.length > 0
              ? baseLabels.join(", ").slice(0, 20)
              : (t.foodType ??
                (t.notes ? t.notes.slice(0, 20) : null) ??
                getMealNameForTime(new Date(treatmentTime)));

          markers.push({
            time: new Date(treatmentTime),
            carbs: baseCarbs,
            treatment: t,
            label: baseLabel,
          });
        }

        for (const [offset, group] of offsetGroups) {
          const offsetTime = treatmentTime + offset * 60 * 1000;
          const offsetLabel =
            group.labels.length > 0
              ? group.labels.join(", ").slice(0, 20)
              : null;

          markers.push({
            time: new Date(offsetTime),
            carbs: group.carbs,
            treatment: t,
            label: offsetLabel,
            isOffset: true,
          });
        }
      } else {
        const time = new Date(treatmentTime);
        const label =
          t.foodType ??
          (t.notes ? t.notes.slice(0, 20) : null) ??
          getMealNameForTime(time);

        markers.push({
          time,
          carbs: t.carbs ?? 0,
          treatment: t,
          label,
        });
      }
    }

    return markers;
  });

  // Treatment handling
  const TREATMENT_PROXIMITY_MS = 5 * 60 * 1000;

  function findAllNearbyTreatments(time: Date): Treatment[] {
    const nearby: Treatment[] = [];

    for (const marker of bolusMarkersForIob) {
      if (
        Math.abs(marker.time.getTime() - time.getTime()) <
        TREATMENT_PROXIMITY_MS
      ) {
        nearby.push(marker.treatment);
      }
    }

    for (const marker of carbMarkersForIob) {
      if (
        Math.abs(marker.time.getTime() - time.getTime()) <
        TREATMENT_PROXIMITY_MS
      ) {
        if (!nearby.some((t) => t._id === marker.treatment._id)) {
          nearby.push(marker.treatment);
        }
      }
    }

    return nearby;
  }

  function handleMarkerClick(treatment: Treatment) {
    const time = new Date(getTreatmentTime(treatment));
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
    return bolusMarkersForIob.find(
      (b) =>
        Math.abs(b.time.getTime() - time.getTime()) < TREATMENT_PROXIMITY_MS
    );
  }

  function findNearbyCarbs(time: Date) {
    return carbMarkersForIob.find(
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
    findActiveSpan(pumpModeSpans, time, false);
  const findActiveOverride = (time: Date) =>
    findActiveSpan(overrideSpans, time, false);
  const findActiveProfile = (time: Date) =>
    findActiveSpan(profileSpans, time, false);
  const findActiveActivities = (time: Date) =>
    findActiveSpan(activitySpans, time, true);
  const findActiveTempBasal = (time: Date) =>
    findActiveSpan(tempBasalSpans, time, false);
  const findActiveBasalDelivery = (time: Date) =>
    findActiveSpan(basalDeliverySpans, time, false);

  function findNearbySystemEvent(time: Date) {
    return systemEvents.find(
      (event) =>
        Math.abs(event.time.getTime() - time.getTime()) < TREATMENT_PROXIMITY_MS
    );
  }
</script>

{#if compact}
  <!-- Compact mode: no card wrapper, just the chart -->
  <div class="h-full w-full @container">
    <div class="h-full">
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
                {tempBasalSpans}
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
                {pumpModeSpans}
                {overrideSpans}
                {profileSpans}
                {activitySpans}
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
              bolusMarkers={bolusMarkersForIob}
              carbMarkers={carbMarkersForIob}
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
                    color={marker.config.color}
                  />
                {/each}
              {/if}

              <!-- System event markers -->
              {#if showAlarms}
                {#each systemEvents as event (event.id)}
                  {@const xPos = context.xScale(event.time)}
                  {@const yPos = context.yScale(
                    glucoseScale(lowThreshold * 0.8)
                  )}
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
                {#each scheduledTrackerMarkers as marker (marker.id)}
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
                    // Prefer state spans for accurate rate lookup
                    const basalDelivery = findActiveBasalDelivery(d.time);
                    if (basalDelivery) {
                      return basalScale(basalDelivery.rate);
                    }
                    // Fallback to chart data series
                    const basal = findBasalValue(basalData, d.time);
                    return basalScale(basal?.rate ?? 0);
                  }}
                  points={{ class: "fill-insulin-basal" }}
                />
              {/if}
            </ChartClipPath>
          </Svg>

          <ChartTooltip
            {context}
            findBasalValue={(time) => findBasalValue(basalData, time)}
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
    <div class={heightClass ?? 'h-80 @md:h-[450px]'}>
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
                {tempBasalSpans}
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
                {pumpModeSpans}
                {overrideSpans}
                {profileSpans}
                {activitySpans}
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
              bolusMarkers={bolusMarkersForIob}
              carbMarkers={carbMarkersForIob}
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
                    color={marker.config.color}
                  />
                {/each}
              {/if}

              <!-- System event markers -->
              {#if showAlarms}
                {#each systemEvents as event (event.id)}
                  {@const xPos = context.xScale(event.time)}
                  {@const yPos = context.yScale(
                    glucoseScale(lowThreshold * 0.8)
                  )}
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
                {#each scheduledTrackerMarkers as marker (marker.id)}
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
                    // Prefer state spans for accurate rate lookup
                    const basalDelivery = findActiveBasalDelivery(d.time);
                    if (basalDelivery) {
                      return basalScale(basalDelivery.rate);
                    }
                    // Fallback to chart data series
                    const basal = findBasalValue(basalData, d.time);
                    return basalScale(basal?.rate ?? 0);
                  }}
                  points={{ class: "fill-insulin-basal" }}
                />
              {/if}
            </ChartClipPath>
          </Svg>

          <ChartTooltip
            {context}
            findBasalValue={(time) => findBasalValue(basalData, time)}
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
      {systemEvents}
      {pumpModeSpans}
      {scheduledTrackerMarkers}
      {currentPumpMode}
      {uniquePumpModes}
      {expandedPumpModes}
      onToggleExpandedPumpModes={() => (expandedPumpModes = !expandedPumpModes)}
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
