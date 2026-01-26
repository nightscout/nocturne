<script lang="ts">
  import {
    type Entry,
    type Treatment,
    type DeviceStatus,
    type TreatmentFood,
    type TrackerInstanceDto,
    type TrackerDefinitionDto,
    type TrackerCategory,
    StateSpanCategory,
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
  import * as Dialog from "$lib/components/ui/dialog";
  import { getRealtimeStore } from "$lib/stores/realtime-store.svelte";
  import {
    Chart,
    Axis,
    Group,
    Polygon,
    Svg,
    Area,
    Spline,
    Points,
    Highlight,
    Text,
    Tooltip,
    AnnotationRange,
    ChartClipPath,
    AnnotationLine,
    Rule,
    AnnotationPoint,
    Rect,
  } from "layerchart";
  import MiniOverviewChart from "./MiniOverviewChart.svelte";
  import RotateCcw from "lucide-svelte/icons/rotate-ccw";
  import { chartConfig, getMealNameForTime } from "$lib/constants";
  import { curveStepAfter, curveMonotoneX, bisector } from "d3";
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
  } from "$lib/data/state-spans.remote";
  import {
    predictionMinutes,
    predictionEnabled,
    predictionDisplayMode,
    glucoseChartLookback,
    GLUCOSE_CHART_FETCH_HOURS,
  } from "$lib/stores/appearance-store.svelte";
  import { bg } from "$lib/utils/formatting";
  import PredictionSettings from "./PredictionSettings.svelte";
  import PredictionVisualizations from "./PredictionVisualizations.svelte";
  import { cn } from "$lib/utils";
  import { goto } from "$app/navigation";
  import {
    SystemEventIcon,
    PumpModeIcon,
    DeviceEventIcon,
    BatteryIcon,
    ReservoirIcon,
    SensorIcon,
    SiteChangeIcon,
    BolusIcon,
    CarbsIcon,
    TrackerCategoryIcon,
    ActivityCategoryIcon,
  } from "$lib/components/icons";
  import Clock from "lucide-svelte/icons/clock";
  import ChevronDown from "lucide-svelte/icons/chevron-down";

  interface ComponentProps {
    entries?: Entry[];
    treatments?: TreatmentWithFoods[];
    deviceStatuses?: DeviceStatus[];
    demoMode?: boolean;
    dateRange?: {
      from: Date | string;
      to: Date | string;
    };
    /**
     * Default basal rate from profile (U/hr) - fallback if server data
     * unavailable
     */
    defaultBasalRate?: number;
    /** Insulin to carb ratio (g per 1U) */
    carbRatio?: number;
    /** Insulin Sensitivity Factor (mg/dL per unit) */
    isf?: number;
    /**
     * Show prediction lines (controlled by both widget toggle and algorithm
     * setting)
     */
    showPredictions?: boolean;
    /** Default focus hours for time range selector (from settings) */
    defaultFocusHours?: number;
    /** Initial visibility for IOB area */
    initialShowIob?: boolean;
    /** Initial visibility for COB area */
    initialShowCob?: boolean;
    /** Initial visibility for Basal track */
    initialShowBasal?: boolean;
    /** Initial visibility for Bolus markers */
    initialShowBolus?: boolean;
    /** Initial visibility for Carb markers */
    initialShowCarbs?: boolean;
    /** Initial visibility for Device Event markers */
    initialShowDeviceEvents?: boolean;
    /** Initial visibility for Alarm/System Event markers */
    initialShowAlarms?: boolean;
    /** Initial visibility for scheduled tracker markers */
    initialShowScheduledTrackers?: boolean;
    /** Initial visibility for override spans (hidden by default) */
    initialShowOverrideSpans?: boolean;
    /** Initial visibility for profile spans (hidden by default) */
    initialShowProfileSpans?: boolean;
    /** Initial visibility for activity spans (hidden by default) */
    initialShowActivitySpans?: boolean;
    /** Optional tracker instances (defaults to realtime store) */
    trackerInstances?: TrackerInstanceDto[];
    /** Optional tracker definitions (defaults to realtime store) */
    trackerDefinitions?: TrackerDefinitionDto[];
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
  }: ComponentProps = $props();

  // Prediction data state
  let predictionData = $state<PredictionData | null>(null);
  let predictionError = $state<string | null>(null);

  // Server-side chart data (IOB, COB, basal)
  let serverChartData = $state<DashboardChartData | null>(null);

  // State span and system event data
  let stateData = $state<ChartStateData | null>(null);

  // Legend toggle state for chart element visibility (initialized from props)
  let showIob = $state(initialShowIob);
  let showCob = $state(initialShowCob);
  let showBasal = $state(initialShowBasal);
  let showBolus = $state(initialShowBolus);
  let showCarbs = $state(initialShowCarbs);
  let showDeviceEvents = $state(initialShowDeviceEvents);
  let showAlarms = $state(initialShowAlarms);
  let showScheduledTrackers = $state(initialShowScheduledTrackers);
  let showOverrideSpans = $state(initialShowOverrideSpans);
  let showProfileSpans = $state(initialShowProfileSpans);
  let showActivitySpans = $state(initialShowActivitySpans);
  let showPumpModes = $state(true);
  let expandedPumpModes = $state(false);

  // Brush/zoom state for the chart (X-axis only)
  // When null, uses the default sliding window (lookbackHours ending at now)
  let brushXDomain = $state<[Date, Date] | null>(null);

  // Whether chart is currently showing a custom time range (not the default sliding window)
  const isZoomed = $derived(brushXDomain !== null);

  // Reset zoom to the default sliding window
  function resetZoom() {
    brushXDomain = null;
  }

  // Handle mini chart brush - saves span for future sessions
  function handleMiniChartBrush(domain: [Date, Date] | null) {
    if (domain) {
      // Calculate the historical span (excluding prediction lookahead)
      // The selection may extend into the future for predictions, but we only
      // save the "lookback" portion as the span preference
      const now = Date.now();
      const selectionEnd = Math.min(domain[1].getTime(), now);
      const spanMs = selectionEnd - domain[0].getTime();
      const spanHours = spanMs / (60 * 60 * 1000);

      // Save the span to persisted settings for use on next page load
      // Round to nearest 0.5 hour for cleaner values
      const roundedSpan = Math.round(spanHours * 2) / 2;
      // Clamp between 1 and 48 hours
      const clampedSpan = Math.max(1, Math.min(48, roundedSpan));
      glucoseChartLookback.current = clampedSpan;

      // Show the selected range now
      brushXDomain = domain;
    } else {
      brushXDomain = null;
    }
  }

  // Browser check for SSR safety (replaces hasMounted pattern)
  const isBrowser = typeof window !== "undefined";

  // Derive prediction fetch trigger - returns null if should not fetch
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
    // Return a stable trigger value
    return { enabled, latestEntryMills };
  });

  // Fetch predictions when trigger changes - with proper cancellation
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

  // Round to minute boundaries to avoid triggering effects every second
  // Defined early because staleBasalData needs it
  const nowMinute = $derived(Math.floor(realtimeStore.now / 60000) * 60000);

  // Calculate most recent basal data source time
  // This is used to detect if the basal data is stale (e.g. from an external service like Glooko that hasn't synced recently)
  const lastBasalSourceTime = $derived.by(() => {
    // Check treatments for temp basals (which indicate active pump communication)
    const lastTempBasal = treatments.find((t) => t.eventType === "Temp Basal");
    // Duration is in minutes, mills is in milliseconds - convert duration to ms
    const lastTempBasalTime =
      lastTempBasal != null
        ? (lastTempBasal.mills ?? 0) + (lastTempBasal.duration ?? 0) * 60 * 1000
        : 0;

    return lastTempBasalTime;
  });
  const STALE_THRESHOLD_MS = 10 * 60 * 1000; // 10 minutes

  const staleBasalData = $derived.by(() => {
    // If no data at all, nothing to mark
    if (lastBasalSourceTime === 0) return null;

    // Use the display range end time to determine staleness
    // When viewing historical data, we use the historical end time, not the current time
    const rangeEndTime = displayDateRange.to.getTime();
    const timeSinceLastUpdate = rangeEndTime - lastBasalSourceTime;

    // Only show stale marker if data is stale AND the last update is within the visible range
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

  function normalizeDate(
    date: Date | string | undefined,
    fallback: Date
  ): Date {
    if (!date) return fallback;
    return date instanceof Date ? date : new Date(date);
  }

  const displayDemoMode = $derived(demoMode ?? realtimeStore.demoMode);

  // Lookback value for view window (from persisted settings) - this is the span width in hours
  const lookbackHours = $derived(glucoseChartLookback.current);

  // Full data range - always fetch 48 hours of data
  const fullDataRange = $derived({
    from: dateRange
      ? normalizeDate(dateRange.from, new Date())
      : new Date(nowMinute - GLUCOSE_CHART_FETCH_HOURS * 60 * 60 * 1000),
    to: dateRange
      ? normalizeDate(dateRange.to, new Date())
      : new Date(nowMinute),
  });

  // Display date range - the visible window, controlled by lookbackHours
  // This is a sliding window that always ends at "now" (or dateRange.to if provided)
  // Note: "to" is just the data end time - prediction extension is added separately
  const displayDateRange = $derived({
    from: dateRange
      ? normalizeDate(dateRange.from, new Date())
      : new Date(nowMinute - lookbackHours * 60 * 60 * 1000),
    to: dateRange
      ? normalizeDate(dateRange.to, new Date())
      : new Date(nowMinute),
  });

  // Display date range including prediction lookahead (for chart view and mini chart selection)
  const displayDateRangeWithPredictions = $derived({
    from: displayDateRange.from,
    to: showPredictions
      ? new Date(
          displayDateRange.to.getTime() + predictionMinutes.current * 60 * 1000
        )
      : displayDateRange.to,
  });

  // Local reactive copy of prediction mode for UI display
  let predictionModeValue = $state(predictionDisplayMode.current);

  // Sync prediction mode changes
  function handlePredictionModeChange(
    value: typeof predictionDisplayMode.current
  ) {
    if (value && value !== predictionModeValue) {
      predictionModeValue = value;
      predictionDisplayMode.current = value;
    }
  }

  // Stable date range for fetching - always fetches full 48 hours
  // Rounds to 5-minute boundaries to prevent rapid re-fetches
  const stableFetchRange = $derived.by(() => {
    if (!isBrowser) return null;
    const fromTime = fullDataRange.from.getTime();
    const toTime = fullDataRange.to.getTime();
    if (isNaN(fromTime) || isNaN(toTime)) return null;
    // Round to 5-minute boundaries for fetch stability
    const intervalMs = 5 * 60 * 1000;
    const startRounded = Math.floor(fromTime / intervalMs) * intervalMs;
    const endRounded = Math.ceil(toTime / intervalMs) * intervalMs;
    return { startTime: startRounded, endTime: endRounded };
  });

  // Fetch server-side chart data when stable range changes - with proper cancellation
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
        if (!cancelled) {
          serverChartData = data;
        }
      })
      .catch((err) => {
        if (!cancelled) {
          console.error("Failed to fetch chart data:", err);
          serverChartData = null;
        }
      });

    // Also fetch state span data
    getChartStateData({ startTime: range.startTime, endTime: range.endTime })
      .then((data) => {
        if (!cancelled) {
          stateData = data;
        }
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

  // Prediction buffer
  const predictionHours = $derived(predictionMinutes.current / 60);

  // Full X domain - all 48 hours of fetched data (used for mini chart)
  const fullXDomain = $derived({
    from: fullDataRange.from,
    to:
      showPredictions && predictionData
        ? new Date(
            fullDataRange.to.getTime() + predictionHours * 60 * 60 * 1000
          )
        : fullDataRange.to,
  });

  // Active X domain - the currently visible window
  // When not zoomed (brushXDomain is null), shows the sliding window based on lookbackHours
  // When zoomed, shows the brush selection
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

  // Filter entries by full data range (48h) - so all data is available for mini chart
  const filteredEntries = $derived(
    entries.filter((e) => {
      const entryTime = e.mills ?? 0;
      return (
        entryTime >= fullDataRange.from.getTime() &&
        entryTime <= fullDataRange.to.getTime()
      );
    })
  );

  // Filter treatments by full data range (48h) - so all data is available for mini chart
  const filteredTreatments = $derived(
    treatments.filter((t) => {
      const treatmentTime =
        t.mills ?? (t.created_at ? new Date(t.created_at).getTime() : 0);
      return (
        treatmentTime >= fullDataRange.from.getTime() &&
        treatmentTime <= fullDataRange.to.getTime()
      );
    })
  );

  // Bolus treatments
  const bolusTreatments = $derived(
    filteredTreatments.filter(
      (t) =>
        t.insulin &&
        t.insulin > 0 &&
        (t.eventType?.includes("Bolus") ||
          t.eventType === "SMB" ||
          t.eventType === "Correction Bolus" ||
          t.eventType === "Meal Bolus" ||
          t.eventType === "Snack Bolus" ||
          t.eventType === "Bolus Wizard" ||
          t.eventType === "Combo Bolus")
    )
  );

  // Carb treatments
  const carbTreatments = $derived(
    filteredTreatments.filter((t) => t.carbs && t.carbs > 0)
  );

  // Device event types for chart markers
  const DEVICE_EVENT_TYPES = [
    "Sensor Start",
    "Sensor Change",
    "Sensor Stop",
    "Site Change",
    "Insulin Change",
    "Pump Battery Change",
  ] as const;

  type DeviceEventType = (typeof DEVICE_EVENT_TYPES)[number];

  // Device event treatments (sensor, site, reservoir, battery changes)
  const deviceEventTreatments = $derived(
    filteredTreatments.filter(
      (t) =>
        t.eventType &&
        DEVICE_EVENT_TYPES.includes(t.eventType as DeviceEventType)
    )
  );

  // Glucose data for chart (convert to display units)
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

  // Calculate median glucose value for positioning device markers
  const medianGlucose = $derived.by(() => {
    if (glucoseData.length === 0) return 100; // Default fallback
    const sorted = [...glucoseData].sort((a, b) => a.sgv - b.sgv);
    const mid = Math.floor(sorted.length / 2);
    return sorted.length % 2 !== 0
      ? sorted[mid].sgv
      : (sorted[mid - 1].sgv + sorted[mid].sgv) / 2;
  });

  // Device event icon configuration
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

  // Device event markers for the chart
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

  // Find nearby device event for tooltip
  function findNearbyDeviceEvent(time: Date) {
    return deviceEventMarkers.find(
      (d) =>
        Math.abs(d.time.getTime() - time.getTime()) < TREATMENT_PROXIMITY_MS
    );
  }

  // Thresholds (convert to display units)
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

  // Use server-side data for IOB and basal, with fallbacks
  const iobData = $derived(serverChartData?.iobSeries ?? []);
  const cobData = $derived(serverChartData?.cobSeries ?? []);
  const basalData = $derived(serverChartData?.basalSeries ?? []);
  const maxIOB = $derived(serverChartData?.maxIob ?? 3);
  const maxBasalRate = $derived(
    serverChartData?.maxBasalRate ?? defaultBasalRate * 2.5
  );

  // Scheduled basal data for the reference line (profile basal without temp modifications)
  const scheduledBasalData = $derived(
    basalData.map((d) => ({
      time: d.time,
      rate: d.scheduledRate ?? d.rate,
    }))
  );

  // Filter pump mode spans to visible range and clip to display bounds
  const pumpModeSpans = $derived.by(() => {
    if (!stateData?.pumpModeSpans) return [];
    const rangeStart = displayDateRange.from.getTime();
    const rangeEnd = displayDateRange.to.getTime();

    return stateData.pumpModeSpans
      .filter((span) => {
        const spanStart = span.startTime.getTime();
        const spanEnd = span.endTime?.getTime() ?? rangeEnd;
        // Include if overlaps with visible range
        return spanEnd > rangeStart && spanStart < rangeEnd;
      })
      .map((span) => ({
        ...span,
        // Clip start/end to visible range
        displayStart: new Date(Math.max(span.startTime.getTime(), rangeStart)),
        displayEnd: new Date(
          Math.min(span.endTime?.getTime() ?? rangeEnd, rangeEnd)
        ),
      }));
  });

  // Get the current (most recent) pump mode state
  const currentPumpMode = $derived.by(() => {
    if (pumpModeSpans.length === 0) return "Automatic";
    // Find the most recent pump mode (highest end time, or the one covering "now")
    const now = Date.now();
    const activeSpan = pumpModeSpans.find((span) => {
      const spanEnd = span.endTime?.getTime() ?? now + 1;
      return span.startTime.getTime() <= now && spanEnd >= now;
    });
    if (activeSpan) return activeSpan.state;
    // If no active span, return the most recent one
    const sorted = [...pumpModeSpans].sort(
      (a, b) => (b.endTime?.getTime() ?? now) - (a.endTime?.getTime() ?? now)
    );
    return sorted[0]?.state ?? "Automatic";
  });

  // Get unique pump modes present in the current view
  const uniquePumpModes = $derived([
    ...new Set(pumpModeSpans.map((s) => s.state)),
  ]);

  // Filter system events to visible range
  const systemEvents = $derived.by(() => {
    if (!stateData?.systemEvents) return [];
    const rangeStart = displayDateRange.from.getTime();
    const rangeEnd = displayDateRange.to.getTime();

    return stateData.systemEvents.filter((event) => {
      const eventTime = event.time.getTime();
      return eventTime >= rangeStart && eventTime <= rangeEnd;
    });
  });

  // Filter temp basal spans to visible range and clip to display bounds
  const tempBasalSpans = $derived.by(() => {
    if (!stateData?.tempBasalSpans) return [];
    const rangeStart = displayDateRange.from.getTime();
    const rangeEnd = displayDateRange.to.getTime();

    return stateData.tempBasalSpans
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
        // Extract rate from metadata
        rate:
          (span.metadata?.rate as number) ??
          (span.metadata?.absolute as number) ??
          null,
        percent: (span.metadata?.percent as number) ?? null,
      }));
  });

  // Filter override spans to visible range and clip to display bounds
  const overrideSpans = $derived.by(() => {
    if (!stateData?.overrideSpans) return [];
    const rangeStart = displayDateRange.from.getTime();
    const rangeEnd = displayDateRange.to.getTime();

    return stateData.overrideSpans
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
  });

  // Filter profile spans to visible range and clip to display bounds
  const profileSpans = $derived.by(() => {
    if (!stateData?.profileSpans) return [];
    const rangeStart = displayDateRange.from.getTime();
    const rangeEnd = displayDateRange.to.getTime();

    return stateData.profileSpans
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
        // Extract profile name from metadata
        profileName: (span.metadata?.profileName as string) ?? span.state,
      }));
  });

  // Filter activity spans (sleep, exercise, illness, travel) to visible range
  const activitySpans = $derived.by(() => {
    if (!stateData?.activitySpans) return [];
    const rangeStart = displayDateRange.from.getTime();
    const rangeEnd = displayDateRange.to.getTime();

    return stateData.activitySpans
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
  });

  // Scheduled tracker expiration markers
  // These show when active trackers (site change, sensor, etc.) are due to expire
  interface ScheduledTrackerMarker {
    id: string;
    definitionId: string;
    name: string;
    category: TrackerCategory;
    time: Date;
    icon?: string;
    color: string;
  }

  // Map tracker category to color
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

  // Filter scheduled tracker expirations to visible range (including prediction window)
  const scheduledTrackerMarkers = $derived.by((): ScheduledTrackerMarker[] => {
    if (!trackerInstances || trackerInstances.length === 0) return [];

    const rangeStart = displayDateRange.from.getTime();
    // Include prediction window to show upcoming expirations
    const rangeEnd = chartXDomain.to.getTime();

    return trackerInstances
      .filter((instance) => {
        // Only include if the tracker has an expected end time
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

  function formatTime(date: Date): string {
    return date.toLocaleTimeString([], { hour: "numeric", minute: "2-digit" });
  }

  // ===== COMPOUND CHART CONFIGURATION =====
  // Swim lane configuration for state span tracks
  // Each swim lane is a thin horizontal band that shows spans of a specific category
  const SWIM_LANE_HEIGHT = 0.04; // 4% of chart height per swim lane

  // Helper function to compute track ratios based on visibility
  // Using a function instead of $derived to avoid reactivity loops
  function getTrackRatios() {
    const showBasalTrack = showBasal;
    const showIobTrack = showIob || showCob;

    // Calculate swim lane visibility and count
    // Activity types share a single combined lane
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
    // Glucose gets the remaining space after basal, IOB, and swim lanes
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
  }

  // Bisector for finding nearest data point
  const bisectDate = bisector((d: { time: Date }) => d.time).left;

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

  // Basal is step-based, so logic is slightly different (value holds until next)
  function findBasalValue(
    series: {
      time: Date;
      rate: number;
      scheduledRate?: number;
      isTemp?: boolean;
    }[],
    time: Date
  ) {
    if (!series || series.length === 0) return undefined;
    const i = bisectDate(series, time, 1);
    return series[i - 1];
  }

  // Treatment marker data for IOB track (includes full treatment for click handling)
  const bolusMarkersForIob = $derived(
    bolusTreatments.map((t) => ({
      time: new Date(getTreatmentTime(t)),
      insulin: t.insulin ?? 0,
      treatment: t,
    }))
  );

  // Build carb markers, accounting for foods with time offsets
  // When a treatment has foods with offsets, create separate markers at each offset time
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

      // Check if we have foods with time offsets
      const foodsWithOffsets = foods.filter(
        (f) => f.timeOffsetMinutes != null && f.timeOffsetMinutes !== 0
      );

      if (foodsWithOffsets.length > 0) {
        // Group foods by their offset time
        const offsetGroups = new Map<
          number,
          { carbs: number; labels: string[] }
        >();

        // Also track foods without offsets (or 0 offset)
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

        // Add any unattributed carbs (carbs not covered by foods) to base time
        const totalFoodCarbs = foods.reduce(
          (sum, f) => sum + (f.carbs ?? 0),
          0
        );
        const treatmentCarbs = t.carbs ?? 0;
        const unattributedCarbs = treatmentCarbs - totalFoodCarbs;
        if (unattributedCarbs > 0) {
          baseCarbs += unattributedCarbs;
        }

        // Create marker for base time (foods with no offset + unattributed)
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

        // Create markers for each offset group
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
        // No offset foods - use original logic
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

  // Treatment edit dialog state
  let selectedTreatment = $state<Treatment | null>(null);
  let isTreatmentDialogOpen = $state(false);
  let isUpdatingTreatment = $state(false);

  // Disambiguation dialog state (when multiple treatments are nearby)
  let nearbyTreatments = $state<Treatment[]>([]);
  let isDisambiguationOpen = $state(false);

  // Find all treatments near a given time (within 5 minute window)
  const TREATMENT_PROXIMITY_MS = 5 * 60 * 1000;

  function findAllNearbyTreatments(time: Date): Treatment[] {
    const nearby: Treatment[] = [];

    // Check bolus treatments
    for (const marker of bolusMarkersForIob) {
      if (
        Math.abs(marker.time.getTime() - time.getTime()) <
        TREATMENT_PROXIMITY_MS
      ) {
        nearby.push(marker.treatment);
      }
    }

    // Check carb treatments
    for (const marker of carbMarkersForIob) {
      if (
        Math.abs(marker.time.getTime() - time.getTime()) <
        TREATMENT_PROXIMITY_MS
      ) {
        // Avoid duplicates (a treatment can have both carbs and insulin)
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
      // Single treatment, open edit dialog directly
      selectedTreatment = treatment;
      isTreatmentDialogOpen = true;
    } else {
      // Multiple treatments nearby, show disambiguation
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

  // Format treatment for display in disambiguation list
  function formatTreatmentSummary(treatment: Treatment): string {
    const parts: string[] = [];
    if (treatment.eventType) parts.push(treatment.eventType);
    if (treatment.insulin) parts.push(`${treatment.insulin}U`);
    if (treatment.carbs) parts.push(`${treatment.carbs}g carbs`);
    return parts.join(" • ") || "Treatment";
  }

  // Find nearby treatments for tooltip display
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

  // Generic helper to find active span(s) at a given time
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

  // Convenience wrappers using the generic helper
  const findActivePumpMode = (time: Date) =>
    findActiveSpan(pumpModeSpans, time, false);
  const findActiveOverride = (time: Date) =>
    findActiveSpan(overrideSpans, time, false);
  const findActiveProfile = (time: Date) =>
    findActiveSpan(profileSpans, time, false);
  const findActiveActivities = (time: Date) =>
    findActiveSpan(activitySpans, time, true);

  // Find nearby system event
  function findNearbySystemEvent(time: Date) {
    return systemEvents.find(
      (event) =>
        Math.abs(event.time.getTime() - time.getTime()) < TREATMENT_PROXIMITY_MS
    );
  }
</script>

{#snippet legendToggle(
  show: boolean,
  toggle: () => void,
  label: string,
  children: import("svelte").Snippet
)}
  <button
    type="button"
    class={cn(
      "flex items-center gap-1 cursor-pointer hover:bg-accent/50 px-1.5 py-0.5 rounded transition-colors",
      !show && "opacity-50"
    )}
    onclick={toggle}
  >
    {@render children()}
    <span class={cn(!show && "line-through")}>{label}</span>
  </button>
{/snippet}

{#snippet legendIndicator(children: import("svelte").Snippet, label: string)}
  <div class="flex items-center gap-1">
    {@render children()}
    <span>{label}</span>
  </div>
{/snippet}

{#snippet glucoseRangeIndicator(colorClass: string, label: string)}
  <div class="flex items-center gap-1">
    <div class="w-2 h-2 rounded-full {colorClass}"></div>
    <span>{label}</span>
  </div>
{/snippet}

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
        <!-- Prediction settings component with its own boundary -->
        <PredictionSettings
          {showPredictions}
          predictionMode={predictionModeValue}
          onPredictionModeChange={handlePredictionModeChange}
        />
      </div>
    </div>
  </CardHeader>

  <CardContent class="p-1 @md:p-2">
    <!-- Zoom indicator and reset button -->
    {#if isZoomed}
      <div
        class="flex items-center justify-between px-4 py-2 mb-2 bg-primary/5 border border-primary/20 rounded-lg"
      >
        <div class="flex items-center gap-2 text-sm text-primary">
          <span class="font-medium">Zoomed view</span>
          {#if brushXDomain}
            <span class="text-xs text-muted-foreground">
              {brushXDomain[0].toLocaleTimeString([], {
                hour: "numeric",
                minute: "2-digit",
              })} - {brushXDomain[1].toLocaleTimeString([], {
                hour: "numeric",
                minute: "2-digit",
              })}
            </span>
          {/if}
        </div>
        <button
          type="button"
          class="flex items-center gap-1 px-2 py-1 text-xs font-medium text-primary bg-primary/10 hover:bg-primary/20 rounded transition-colors"
          onclick={resetZoom}
        >
          <RotateCcw size={12} />
          Reset zoom
        </button>
      </div>
    {/if}

    <!-- Single compound chart with remapped scales for basal, glucose, and IOB -->
    <div class="h-80 @md:h-[450px] p-2 @md:p-4">
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
            <!-- Get track configuration (ratios and visibility flags) -->
            {@const trackConfig = getTrackRatios()}
            {@const { showBasalTrack, showIobTrack, swimLanes } = trackConfig}

            <!-- Create remapped scales for basal, glucose, IOB, and swim lane tracks -->
            <!-- Layout from top to bottom: BASAL | SWIM LANES | GLUCOSE | IOB -->
            {@const basalTrackHeight = context.height * trackConfig.basal}
            {@const swimLaneHeight = context.height * SWIM_LANE_HEIGHT}
            {@const glucoseTrackHeight = context.height * trackConfig.glucose}
            {@const iobTrackHeight = context.height * trackConfig.iob}

            <!-- Track positions (y coordinates in SVG where 0 = top) -->
            {@const basalTrackTop = 0}
            {@const basalTrackBottom = basalTrackHeight}

            <!-- Swim lanes positioned between basal and glucose -->
            <!-- Calculate positions for each visible swim lane -->
            {@const swimLanePositions = (() => {
              let currentY = basalTrackBottom;
              // @ts-ignore
              const positions: Record = {};
              const laneOrder = [
                "pumpMode",
                "override",
                "profile",
                "activity",
              ] as const;
              for (const lane of laneOrder) {
                const visible = swimLanes[lane];
                positions[lane] = {
                  top: currentY,
                  bottom: visible ? currentY + swimLaneHeight : currentY,
                  visible,
                };
                if (visible) currentY += swimLaneHeight;
              }
              return positions;
            })()}

            {@const swimLanesBottom =
              basalTrackBottom + trackConfig.swimLanesRatio * context.height}
            {@const glucoseTrackTop = swimLanesBottom}
            {@const glucoseTrackBottom = glucoseTrackTop + glucoseTrackHeight}
            {@const iobTrackTop = glucoseTrackBottom}
            {@const iobTrackBottom = iobTrackTop + iobTrackHeight}

            <!-- The Chart's internal yScale maps [0, glucoseYMax] -> [height, 0] (standard D3 convention: 0 at bottom, max at top) -->
            <!-- We need to create scales that output VALUES in the Chart's domain, so that when Chart applies its yScale, we get correct pixels -->

            <!-- Chart's yScale: domain=[0, glucoseYMax], range=[height, 0] -->
            <!-- So yScale(0) = height (bottom), yScale(glucoseYMax) = 0 (top) -->
            <!-- To place something at pixel Y, we need to find the data value V where yScale(V) = Y -->
            <!-- yScale inverse: pixelToData(Y) = glucoseYMax * (1 - Y/height) -->

            <!-- Helper: convert a pixel Y position to the glucose data domain value that will render at that Y -->
            {@const pixelToGlucoseDomain = (pixelY: number) =>
              glucoseYMax * (1 - pixelY / context.height)}

            <!-- Basal scale: TOP track, 0 at top (pixel 0), max at bottom (pixel basalTrackBottom) -->
            <!-- Returns glucose-domain values that Chart will convert to correct pixels -->
            {@const basalScale = (rate: number) => {
              const pixelY =
                basalTrackTop + (rate / maxBasalRate) * basalTrackHeight;
              return pixelToGlucoseDomain(pixelY);
            }}
            {@const basalZero = pixelToGlucoseDomain(basalTrackTop)}
            <!-- D3 scale for basal Axis (maps rate -> pixel Y directly) -->
            {@const basalAxisScale = scaleLinear()
              .domain([0, maxBasalRate])
              .range([basalTrackTop, basalTrackBottom])}

            <!-- Glucose scale: MIDDLE track, 0 at bottom of glucose track, max at top -->
            {@const glucoseScale = scaleLinear()
              .domain([0, glucoseYMax])
              .range([
                pixelToGlucoseDomain(glucoseTrackBottom),
                pixelToGlucoseDomain(glucoseTrackTop),
              ])}
            <!-- D3 scale for glucose Axis (maps glucose -> pixel Y directly) -->
            {@const glucoseAxisScale = scaleLinear()
              .domain([0, glucoseYMax])
              .range([glucoseTrackBottom, glucoseTrackTop])}

            <!-- IOB scale: BOTTOM track, 0 at bottom (pixel iobTrackBottom), max at top (pixel iobTrackTop) -->
            {@const iobScale = (value: number) => {
              const pixelY = iobTrackBottom - (value / maxIOB) * iobTrackHeight;
              return pixelToGlucoseDomain(pixelY);
            }}
            {@const iobZero = pixelToGlucoseDomain(iobTrackBottom)}
            <!-- D3 scale for IOB Axis (maps IOB -> pixel Y directly) -->
            {@const iobAxisScale = scaleLinear()
              .domain([0, maxIOB])
              .range([iobTrackBottom, iobTrackTop])}

            <ChartClipPath>
              <!-- ===== BASAL TRACK (TOP) ===== -->
              <!-- Temp basal span indicators (shown in basal track when basal is visible) -->
              {#if showBasal}
                {#each tempBasalSpans as span (span.id)}
                  <AnnotationRange
                    x={[span.displayStart.getTime(), span.displayEnd.getTime()]}
                    y={[
                      basalScale(maxBasalRate * 0.9),
                      basalScale(maxBasalRate * 0.7),
                    ]}
                    fill={span.color}
                    class="opacity-40"
                  />
                  <!-- Show temp basal rate label -->
                  {#if span.rate !== null}
                    <Group
                      x={context.xScale(span.displayStart)}
                      y={context.yScale(basalScale(maxBasalRate * 0.8))}
                    >
                      <Text
                        x={4}
                        y={0}
                        class="text-[7px] fill-insulin-basal font-medium"
                      >
                        {span.rate.toFixed(2)}U/h
                      </Text>
                    </Group>
                  {:else if span.percent !== null}
                    <Group
                      x={context.xScale(span.displayStart)}
                      y={context.yScale(basalScale(maxBasalRate * 0.8))}
                    >
                      <Text
                        x={4}
                        y={0}
                        class="text-[7px] fill-insulin-basal font-medium"
                      >
                        {span.percent}%
                      </Text>
                    </Group>
                  {/if}
                {/each}
              {/if}

              <!-- ===== SWIM LANES (between Basal and Glucose) ===== -->
              <!-- Each swim lane renders spans for a specific category as horizontal bands -->

              <!-- Pump Mode Swim Lane -->
              {#if swimLanePositions.pumpMode.visible}
                {@const lane = swimLanePositions.pumpMode}
                <!-- Lane background -->
                <Rect
                  x={0}
                  y={lane.top}
                  width={context.width}
                  height={lane.bottom - lane.top}
                  fill="var(--muted)"
                  class="opacity-20"
                />
                <!-- Lane label -->
                <Text
                  x={4}
                  y={lane.top + (lane.bottom - lane.top) / 2 + 3}
                  class="text-[7px] fill-muted-foreground font-medium"
                >
                  MODE
                </Text>
                <!-- Pump mode spans -->
                {#each pumpModeSpans as span (span.id)}
                  {@const spanXPos = context.xScale(span.displayStart)}
                  <Rect
                    x={spanXPos}
                    y={lane.top + 1}
                    width={context.xScale(span.displayEnd) - spanXPos}
                    height={lane.bottom - lane.top - 2}
                    fill={span.color}
                    class="opacity-60"
                    rx="2"
                  />
                  <!-- Icon at start of span -->
                  <Group
                    x={spanXPos}
                    y={lane.top + (lane.bottom - lane.top) / 2}
                  >
                    <foreignObject x="2" y="-6" width="12" height="12">
                      <div
                        class="flex items-center justify-center w-full h-full"
                      >
                        <PumpModeIcon
                          state={span.state}
                          size={10}
                          color={span.color}
                        />
                      </div>
                    </foreignObject>
                  </Group>
                {/each}
              {/if}

              <!-- Override Swim Lane -->
              {#if swimLanePositions.override.visible}
                {@const lane = swimLanePositions.override}
                <!-- Lane background -->
                <Rect
                  x={0}
                  y={lane.top}
                  width={context.width}
                  height={lane.bottom - lane.top}
                  fill="var(--muted)"
                  class="opacity-20"
                />
                <!-- Lane label -->
                <Text
                  x={4}
                  y={lane.top + (lane.bottom - lane.top) / 2 + 3}
                  class="text-[7px] fill-muted-foreground font-medium"
                >
                  OVERRIDE
                </Text>
                <!-- Override spans -->
                {#each overrideSpans as span (span.id)}
                  {@const spanXPos = context.xScale(span.displayStart)}
                  <Rect
                    x={spanXPos}
                    y={lane.top + 1}
                    width={context.xScale(span.displayEnd) - spanXPos}
                    height={lane.bottom - lane.top - 2}
                    fill={span.color}
                    class="opacity-50"
                    rx="2"
                  />
                  <!-- State label -->
                  <Text
                    x={spanXPos + 4}
                    y={lane.top + (lane.bottom - lane.top) / 2 + 3}
                    class="text-[6px] fill-foreground font-medium"
                  >
                    {span.state}
                  </Text>
                {/each}
              {/if}

              <!-- Profile Swim Lane -->
              {#if swimLanePositions.profile.visible}
                {@const lane = swimLanePositions.profile}
                <!-- Lane background -->
                <Rect
                  x={0}
                  y={lane.top}
                  width={context.width}
                  height={lane.bottom - lane.top}
                  fill="var(--muted)"
                  class="opacity-20"
                />
                <!-- Lane label -->
                <Text
                  x={4}
                  y={lane.top + (lane.bottom - lane.top) / 2 + 3}
                  class="text-[7px] fill-muted-foreground font-medium"
                >
                  PROFILE
                </Text>
                <!-- Profile spans -->
                {#each profileSpans as span (span.id)}
                  {@const spanXPos = context.xScale(span.displayStart)}
                  <Rect
                    x={spanXPos}
                    y={lane.top + 1}
                    width={context.xScale(span.displayEnd) - spanXPos}
                    height={lane.bottom - lane.top - 2}
                    fill={span.color}
                    class="opacity-30"
                    rx="2"
                  />
                  <!-- Profile name label -->
                  <Text
                    x={spanXPos + 4}
                    y={lane.top + (lane.bottom - lane.top) / 2 + 3}
                    class="text-[6px] fill-foreground font-medium"
                  >
                    {span.profileName}
                  </Text>
                {/each}
              {/if}

              <!-- Activity Swim Lane (Sleep, Exercise, Illness, Travel - all in one lane) -->
              {#if swimLanePositions.activity?.visible}
                {@const lane = swimLanePositions.activity}
                <!-- Lane background -->
                <Rect
                  x={0}
                  y={lane.top}
                  width={context.width}
                  height={lane.bottom - lane.top}
                  fill="var(--muted)"
                  class="opacity-10"
                />
                <!-- Lane label -->
                <Text
                  x={4}
                  y={lane.top + (lane.bottom - lane.top) / 2 + 3}
                  class="text-[7px] fill-muted-foreground font-medium"
                >
                  ACTIVITY
                </Text>
                <!-- All activity spans rendered in the same lane -->
                {#each activitySpans as span (span.id)}
                  {@const spanXPos = context.xScale(span.displayStart)}
                  <Rect
                    x={spanXPos}
                    y={lane.top + 1}
                    width={context.xScale(span.displayEnd) - spanXPos}
                    height={lane.bottom - lane.top - 2}
                    fill={span.color}
                    class="opacity-50"
                    rx="2"
                  />
                  <!-- Icon at start -->
                  <Group
                    x={spanXPos}
                    y={lane.top + (lane.bottom - lane.top) / 2}
                  >
                    <foreignObject x="2" y="-6" width="12" height="12">
                      <div
                        class="flex items-center justify-center w-full h-full"
                      >
                        <ActivityCategoryIcon
                          category={span.category}
                          size={10}
                          color={span.color}
                        />
                      </div>
                    </foreignObject>
                  </Group>
                {/each}
              {/if}

              {#if staleBasalData}
                <ChartClipPath>
                  <AnnotationRange
                    x={[
                      staleBasalData.start.getTime(),
                      staleBasalData.end.getTime(),
                    ]}
                    y={[basalScale(maxBasalRate), basalZero]}
                    pattern={{
                      size: 8,
                      lines: {
                        rotate: -45,
                        opacity: 0.1,
                      },
                    }}
                  />
                </ChartClipPath>
                <AnnotationLine
                  x={staleBasalData.start}
                  class="stroke-yellow-500/50 stroke-1"
                  stroke-dasharray="2,2"
                />
                <AnnotationPoint
                  x={staleBasalData.start.getTime()}
                  y={basalScale(maxBasalRate)}
                  label="Last pump sync"
                  labelPlacement="bottom-right"
                  fill="yellow"
                  class="hover:bg-background hover:text-foreground"
                />
              {/if}

              <!-- Scheduled basal rate line -->
              {#if scheduledBasalData.length > 0 && showBasal}
                <Spline
                  data={scheduledBasalData}
                  x={(d) => d.time}
                  y={(d) => basalScale(d.rate)}
                  curve={curveStepAfter}
                  class="stroke-muted-foreground/50 stroke-1 fill-none"
                  stroke-dasharray="4,4"
                />
              {/if}

              <!-- Basal axis on right -->
              {#if showBasalTrack}
                <Axis
                  placement="right"
                  scale={basalAxisScale}
                  ticks={2}
                  tickLabelProps={{
                    class: "text-[9px] fill-muted-foreground",
                  }}
                />

                <!-- Basal track label -->
                <Text
                  x={4}
                  y={basalTrackTop + 12}
                  class="text-[8px] fill-muted-foreground font-medium"
                >
                  BASAL
                </Text>
              {/if}

              <!-- Effective basal area (drips down from top of basal track) -->
              <!-- y0 = baseline (0 rate at top), y1 = actual rate (grows down) -->
              {#if basalData.length > 0 && showBasal}
                <!-- <LinearGradient
                class="from-insulin-basal/95 to-insulin-basal/5"
                vertical
              > -->
                <Area
                  data={basalData}
                  x={(d) => d.time}
                  y0={() => basalZero}
                  y1={(d) => basalScale(d.rate)}
                  curve={curveStepAfter}
                  fill="var(--insulin-basal)"
                  class="stroke-insulin stroke-1"
                />
                <!-- </LinearGradient> -->
              {/if}
            </ChartClipPath>

            <!-- ===== GLUCOSE TRACK (MIDDLE) ===== -->
            <!-- High threshold line -->
            <Rule
              y={glucoseScale(highThreshold)}
              class="stroke-glucose-high/50"
              stroke-dasharray="4,4"
            />

            <!-- Low threshold line -->
            <Rule
              y={glucoseScale(lowThreshold)}
              class="stroke-glucose-very-low/50"
              stroke-dasharray="4,4"
            />

            <!-- Glucose axis on left -->
            <Axis
              placement="left"
              scale={glucoseAxisScale}
              ticks={5}
              tickLabelProps={{ class: "text-xs fill-muted-foreground" }}
            />

            <ChartClipPath>
              <!-- Glucose line -->
              <Spline
                data={glucoseData}
                x={(d) => d.time}
                y={(d) => glucoseScale(d.sgv)}
                class="stroke-glucose-in-range stroke-2 fill-none"
                motion="spring"
                curve={curveMonotoneX}
              />

              <!-- Glucose points - only show when density is reasonable (less than 0.5 points per pixel) -->
              <!-- This prevents points from smashing together on multi-day views -->
              {@const pointDensity = glucoseData.length / context.width}
              {@const showGlucosePoints = pointDensity < 0.5}
              {#if showGlucosePoints}
                {#each glucoseData as point}
                  <Points
                    data={[point]}
                    x={(d) => d.time}
                    y={(d) => glucoseScale(d.sgv)}
                    r={3}
                    fill={point.color}
                    class="opacity-90"
                  />
                {/each}
              {/if}

              <!-- Prediction visualizations -->
              <PredictionVisualizations
                {showPredictions}
                {predictionData}
                predictionEnabled={predictionEnabled.current}
                predictionDisplayMode={predictionDisplayMode.current}
                {predictionError}
                {glucoseScale}
                {glucoseTrackTop}
                {chartXDomain}
                {glucoseData}
              />
            </ChartClipPath>
            <!-- ===== IOB TRACK (BOTTOM) with Treatment Markers ===== -->
            {#if showIobTrack}
              <!-- IOB axis on right -->
              <Axis
                placement="right"
                scale={iobAxisScale}
                ticks={2}
                tickLabelProps={{ class: "text-[9px] fill-muted-foreground" }}
              />

              <!-- IOB/COB track label -->
              <Text
                x={4}
                y={iobTrackTop + 12}
                class="text-[8px] fill-muted-foreground font-medium"
              >
                IOB/COB
              </Text>
            {/if}

            <ChartClipPath>
              <!-- COB area (scaled by carb ratio to show on IOB-equivalent scale) -->
              {#if cobData.length > 0 && cobData.some((d) => d.value > 0.01) && showCob}
                <Area
                  data={cobData}
                  x={(d) => d.time}
                  y0={() => iobZero}
                  y1={(d) => iobScale(d.value / carbRatio)}
                  motion="spring"
                  curve={curveMonotoneX}
                  fill=""
                  class="fill-carbs/40"
                />
              {/if}

              <!-- IOB area (grows up from bottom of IOB track) -->
              {#if iobData.length > 0 && iobData.some((d) => d.value > 0.01) && showIob}
                <Area
                  data={iobData}
                  x={(d) => d.time}
                  y0={() => iobZero}
                  y1={(d) => iobScale(d.value)}
                  motion="spring"
                  curve={curveMonotoneX}
                  fill=""
                  class="fill-iob-basal/60"
                />
              {/if}
            </ChartClipPath>

            <!-- X-Axis (bottom) -->
            <Axis
              placement="bottom"
              format={"hour"}
              tickLabelProps={{ class: "text-xs fill-muted-foreground" }}
            />

            <ChartClipPath>
              <!-- Bolus markers (on top layer) - clickable to edit -->
              <!-- Hemisphere (semicircle pointing down) normally, triangle if manual override -->
              {#if showBolus}
                {#each bolusMarkersForIob as marker}
                  {@const xPos = context.xScale(marker.time)}
                  {@const yPos = context.yScale(iobScale(marker.insulin))}
                  {@const t = marker.treatment}
                  {@const suggestedTotal =
                    (t.insulinRecommendationForCarbs ?? 0) +
                    (t.insulinRecommendationForCorrection ?? 0)}
                  {@const hasSuggestion = suggestedTotal > 0}
                  {@const isOverride =
                    hasSuggestion &&
                    Math.abs(suggestedTotal - marker.insulin) > 0.05}
                  <Group
                    x={xPos}
                    y={yPos + 0}
                    onclick={() => handleMarkerClick(marker.treatment)}
                    class="cursor-pointer"
                  >
                    {#if isOverride}
                      <!-- Triangle for manual override -->
                      <Polygon
                        points={[
                          { x: 0, y: 12 },
                          { x: -8, y: 0 },
                          { x: 8, y: 0 },
                        ]}
                        class="opacity-90 fill-insulin-bolus hover:opacity-100 transition-opacity"
                      />
                    {:else}
                      <!-- Hemisphere (dome shape - curves above baseline) -->
                      <path
                        d="M -8,0 A 8,8 0 0,0 8,0 Z"
                        class="opacity-90 fill-insulin-bolus hover:opacity-100 transition-opacity"
                      />
                    {/if}
                    <Text
                      y={-14}
                      textAnchor="middle"
                      class="text-[8px] fill-insulin-bolus font-medium"
                    >
                      {marker.insulin.toFixed(1)}U
                    </Text>
                  </Group>
                {/each}
              {/if}

              <!-- Carb markers (on top layer) - clickable to edit -->
              <!-- Hemisphere (semicircle pointing up) to complement bolus hemispheres -->
              {#if showCarbs}
                {#each carbMarkersForIob as marker}
                  {@const xPos = context.xScale(marker.time)}
                  {@const yPos = context.yScale(
                    iobScale(marker.carbs / carbRatio)
                  )}
                  <Group
                    x={xPos}
                    y={yPos}
                    onclick={() => handleMarkerClick(marker.treatment)}
                    class="cursor-pointer"
                  >
                    <!-- Food/meal label above the marker -->
                    {#if marker.label}
                      <Text
                        y={-18}
                        textAnchor="middle"
                        class="text-[7px] fill-carbs font-medium opacity-80"
                      >
                        {marker.label}
                      </Text>
                    {/if}
                    <!-- Hemisphere (bowl shape - curves below baseline) -->
                    <path
                      d="M -8,0 A 8,8 0 0,1 8,0 Z"
                      fill="var(--carbs)"
                      class="opacity-90 hover:opacity-100 transition-opacity"
                    />
                    <Text
                      y={18}
                      textAnchor="middle"
                      class="text-[8px] fill-carbs font-medium"
                    >
                      {marker.carbs}g
                    </Text>
                  </Group>
                {/each}
              {/if}

              <!-- Device event markers (positioned at median glucose in glucose track) -->
              {#if showDeviceEvents}
                {#each deviceEventMarkers as marker}
                  {@const xPos = context.xScale(marker.time)}
                  {@const yPos = context.yScale(glucoseScale(medianGlucose))}
                  <Group x={xPos} y={yPos}>
                    <!-- Background circle -->
                    <circle
                      r="12"
                      fill="var(--background)"
                      stroke={marker.config.color}
                      stroke-width="2"
                      class="opacity-95"
                    />
                    <!-- Icon using foreignObject to embed Lucide component -->
                    <foreignObject x="-10" y="-10" width="20" height="20">
                      <div
                        class="flex items-center justify-center w-full h-full"
                      >
                        <DeviceEventIcon
                          eventType={marker.eventType}
                          size={16}
                          color={marker.config.color}
                        />
                      </div>
                    </foreignObject>
                  </Group>
                {/each}
              {/if}

              <!-- System event markers (alarms, warnings) positioned at glucose track bottom -->
              {#if showAlarms}
                {#each systemEvents as event (event.id)}
                  {@const xPos = context.xScale(event.time)}
                  {@const yPos = context.yScale(
                    glucoseScale(lowThreshold * 0.8)
                  )}
                  <Group x={xPos} y={yPos}>
                    <!-- Icon using foreignObject to embed Lucide component -->
                    <foreignObject x="-8" y="-8" width="16" height="16">
                      <div
                        class="flex items-center justify-center w-full h-full"
                      >
                        <SystemEventIcon
                          eventType={event.eventType}
                          size={16}
                          color={event.color}
                        />
                      </div>
                    </foreignObject>
                  </Group>
                {/each}
              {/if}

              <!-- Scheduled tracker expiration markers (dashed vertical lines) -->
              {#if showScheduledTrackers}
                {#each scheduledTrackerMarkers as marker (marker.id)}
                  {@const xPos = context.xScale(marker.time)}
                  {@const lineTop = basalTrackTop + 20}
                  {@const lineBottom = context.height}
                  <!-- Dashed vertical line spanning the chart height -->
                  <line
                    x1={xPos}
                    y1={lineTop}
                    x2={xPos}
                    y2={lineBottom}
                    stroke={marker.color}
                    stroke-width="1.5"
                    stroke-dasharray="4,4"
                    class="opacity-60"
                  />
                  <!-- Icon and label at the top of the basal track -->
                  <Group x={xPos} y={basalTrackTop + 10}>
                    <!-- Background pill -->
                    <Rect
                      x={-24}
                      y={-8}
                      width={48}
                      height={16}
                      rx="8"
                      fill="var(--background)"
                      stroke={marker.color}
                      stroke-width="1"
                      class="opacity-90"
                    />
                    <!-- Icon using foreignObject -->
                    <foreignObject x="-22" y="-6" width="12" height="12">
                      <div
                        class="flex items-center justify-center w-full h-full"
                      >
                        <TrackerCategoryIcon
                          category={marker.category}
                          size={10}
                          color={marker.color}
                        />
                      </div>
                    </foreignObject>
                    <!-- Time label -->
                    <Text
                      x={3}
                      y={0}
                      textAnchor="start"
                      class="text-[7px] fill-muted-foreground font-medium"
                      dy="0.35em"
                    >
                      {marker.time.toLocaleTimeString([], {
                        hour: "numeric",
                        minute: "2-digit",
                      })}
                    </Text>
                  </Group>
                {/each}
              {/if}

              <!-- Basal highlight with remapped scale -->
              {#if showBasal}
                <Highlight
                  x={(d) => d.time}
                  y={(d) => {
                    const basal = findBasalValue(basalData, d.time);
                    return basalScale(basal?.rate ?? 0);
                  }}
                  points={{ class: "fill-insulin-basal" }}
                />
              {/if}

              <!-- COB highlight with remapped scale (scaled by carb ratio) -->
              {#if showCob}
                <Highlight
                  x={(d) => d.time}
                  y={(d) => {
                    const cob = findSeriesValue(cobData, d.time);
                    if (!cob || cob.value <= 0) return null;
                    return iobScale(cob.value / carbRatio);
                  }}
                  points={{ class: "fill-carbs" }}
                />
              {/if}

              <!-- IOB highlight with remapped scale -->
              {#if showIob}
                <Highlight
                  x={(d) => d.time}
                  y={(d) => {
                    const iob = findSeriesValue(iobData, d.time);
                    if (!iob || iob.value <= 0) return null;
                    return iobScale(iob.value);
                  }}
                  points={{ class: "fill-iob-basal" }}
                />
              {/if}
              <!-- Glucose highlight (main) -->
              <Highlight
                x={(d) => d.time}
                y={(d) => glucoseScale(d.sgv)}
                points
                lines
              />
            </ChartClipPath>
          </Svg>

          <Tooltip.Root
            {context}
            class="bg-popover/95 border border-border rounded-lg shadow-xl text-xs z-50 backdrop-blur-sm"
          >
            {#snippet children({ data })}
              {@const activeBasal = findBasalValue(basalData, data.time)}
              {@const activeIob = findSeriesValue(iobData, data.time)}
              {@const activeCob = findSeriesValue(cobData, data.time)}
              {@const activePumpMode = findActivePumpMode(data.time)}
              {@const activeOverride = findActiveOverride(data.time)}
              {@const activeProfile = findActiveProfile(data.time)}
              {@const activeActivities = findActiveActivities(data.time)}
              {@const nearbyBolus = findNearbyBolus(data.time)}
              {@const nearbyCarbs = findNearbyCarbs(data.time)}
              {@const nearbyDeviceEvent = findNearbyDeviceEvent(data.time)}
              {@const nearbySystemEvent = findNearbySystemEvent(data.time)}

              <Tooltip.Header
                value={data?.time}
                format="minute"
                class="text-popover-foreground border-b border-border pb-1 mb-1 text-sm font-semibold"
              />
              <Tooltip.List>
                {#if data?.sgv}
                  <Tooltip.Item
                    label="Glucose"
                    value={data.sgv}
                    format="integer"
                    color="var(--glucose-in-range)"
                    class="text-popover-foreground font-bold"
                  />
                {/if}
                {#if showBolus && nearbyBolus}
                  <Tooltip.Item
                    label="Bolus"
                    value={`${nearbyBolus.insulin.toFixed(1)}U`}
                    color="var(--insulin-bolus)"
                    class="font-medium"
                  />
                {/if}
                {#if showCarbs && nearbyCarbs}
                  <Tooltip.Item
                    label="Carbs"
                    value={`${nearbyCarbs.carbs}g`}
                    color="var(--carbs)"
                    class="font-medium"
                  />
                {/if}
                {#if showDeviceEvents && nearbyDeviceEvent}
                  <Tooltip.Item
                    label={nearbyDeviceEvent.eventType}
                    value={nearbyDeviceEvent.notes || ""}
                    color={nearbyDeviceEvent.config.color}
                    class="font-medium"
                  />
                {/if}
                {#if showIob && activeIob}
                  <Tooltip.Item
                    label="IOB"
                    value={activeIob.value}
                    format={"decimal"}
                    color="var(--iob-basal)"
                  />
                {/if}
                {#if showCob && activeCob && activeCob.value > 0}
                  <Tooltip.Item
                    label="COB"
                    value={`${activeCob.value.toFixed(0)}g`}
                    color="var(--carbs)"
                  />
                {/if}
                {#if showBasal && activeBasal}
                  {@const isEffectiveTemp =
                    activeBasal.isTemp &&
                    activeBasal.rate !== activeBasal.scheduledRate}
                  <Tooltip.Item
                    label={isEffectiveTemp ? "Temp Basal" : "Basal"}
                    value={activeBasal.rate}
                    format={"decimal"}
                    color={isEffectiveTemp
                      ? "var(--insulin-temp-basal)"
                      : "var(--insulin-basal)"}
                    class={cn(
                      staleBasalData && data.time >= staleBasalData.start
                        ? "text-yellow-500 font-bold"
                        : ""
                    )}
                  />
                  {#if isEffectiveTemp && activeBasal.scheduledRate !== undefined}
                    <Tooltip.Item
                      label="Scheduled"
                      value={activeBasal.scheduledRate}
                      format={"decimal"}
                      color="var(--muted-foreground)"
                    />
                  {/if}
                {/if}
                {#if showPumpModes && activePumpMode}
                  <Tooltip.Item
                    label="Pump Mode"
                    value={activePumpMode.state}
                    color={activePumpMode.color}
                    class="font-medium"
                  />
                {/if}
                {#if showOverrideSpans && activeOverride}
                  <Tooltip.Item
                    label="Override"
                    value={activeOverride.state}
                    color={activeOverride.color}
                    class="font-medium"
                  />
                {/if}
                {#if showProfileSpans && activeProfile}
                  <Tooltip.Item
                    label="Profile"
                    value={activeProfile.profileName}
                    color={activeProfile.color}
                  />
                {/if}
                {#if showActivitySpans}
                  {#each activeActivities as activity (activity.id)}
                    <Tooltip.Item
                      label={activity.category}
                      value={activity.state}
                      color={activity.color}
                      class="font-medium"
                    />
                  {/each}
                {/if}
                {#if showAlarms && nearbySystemEvent}
                  <Tooltip.Item
                    label={nearbySystemEvent.eventType}
                    value={nearbySystemEvent.description ||
                      nearbySystemEvent.code ||
                      ""}
                    color={nearbySystemEvent.color}
                    class="font-medium"
                  />
                {/if}
              </Tooltip.List>
            {/snippet}
          </Tooltip.Root>
          <Tooltip.Root
            x="data"
            y={context.height + context.padding.top}
            yOffset={2}
            anchor="top"
            variant="none"
            class="text-sm font-semibold leading-3 px-2 py-1 rounded-sm whitespace-nowrap bg-background"
          >
            {#snippet children({ data })}
              <Tooltip.Item
                value={data?.time}
                format="minute"
                onclick={() =>
                  goto(`/reports/day-in-review?date=${data?.time}`)}
              />
            {/snippet}
          </Tooltip.Root>
        {/snippet}
      </Chart>
    </div>

    <!-- Mini Overview Chart for zoom navigation - always visible to show full 48h context -->
    {#if glucoseData.length > 0}
      {@const miniPredictionData =
        showPredictions && predictionData?.curves?.main
          ? predictionData.curves.main.map((p) => ({
              time: new Date(p.timestamp),
              value: Number(bg(p.value)),
            }))
          : null}
      {@const miniSelectedDomain: [Date, Date] = brushXDomain ?? [displayDateRangeWithPredictions.from, displayDateRangeWithPredictions.to]}
      <MiniOverviewChart
        data={glucoseData}
        fullXDomain={[fullXDomain.from, fullXDomain.to]}
        selectedXDomain={miniSelectedDomain}
        yDomain={[0, glucoseYMax]}
        expanded={true}
        highThreshold={Number(highThreshold)}
        lowThreshold={Number(lowThreshold)}
        onSelectionChange={(domain) => {
          // Save span for future sessions (mini chart controls the persistent span)
          handleMiniChartBrush(domain);
        }}
        predictionData={miniPredictionData}
        showPredictions={showPredictions && predictionEnabled.current}
      />
    {/if}

    <!-- Legend -->
    <div
      class="flex flex-wrap justify-center gap-4 text-sm text-muted-foreground pt-2"
    >
      {@render glucoseRangeIndicator("bg-glucose-in-range", "In Range")}
      {#if glucoseData.some((d) => d.sgv > veryHighThreshold)}
        {@render glucoseRangeIndicator("bg-glucose-very-high", "Very High")}
      {/if}
      {#if glucoseData.some((d) => d.sgv > highThreshold && d.sgv <= veryHighThreshold)}
        {@render glucoseRangeIndicator("bg-glucose-high", "High")}
      {/if}
      {#if glucoseData.some((d) => d.sgv < lowThreshold && d.sgv >= veryLowThreshold)}
        {@render glucoseRangeIndicator("bg-glucose-low", "Low")}
      {/if}
      {#if glucoseData.some((d) => d.sgv < veryLowThreshold)}
        {@render glucoseRangeIndicator("bg-glucose-very-low", "Very Low")}
      {/if}
      {#snippet basalIcon()}<div
          class="w-3 h-2 bg-insulin-basal border border-insulin"
        ></div>{/snippet}
      {#snippet iobIcon()}<div
          class="w-3 h-2 bg-iob-basal border border-insulin"
        ></div>{/snippet}
      {#snippet cobIcon()}<div
          class="w-3 h-2 bg-carbs/40 border border-carbs"
        ></div>{/snippet}
      {#snippet bolusIcon()}<BolusIcon size={16} />{/snippet}
      {#snippet carbsIcon()}<CarbsIcon size={16} />{/snippet}
      {@render legendToggle(
        showBasal,
        () => (showBasal = !showBasal),
        "Basal",
        basalIcon
      )}
      {@render legendToggle(
        showIob,
        () => (showIob = !showIob),
        "IOB",
        iobIcon
      )}
      {@render legendToggle(
        showCob,
        () => (showCob = !showCob),
        "COB",
        cobIcon
      )}
      {@render legendToggle(
        showBolus,
        () => (showBolus = !showBolus),
        "Bolus",
        bolusIcon
      )}
      {@render legendToggle(
        showCarbs,
        () => (showCarbs = !showCarbs),
        "Carbs",
        carbsIcon
      )}
      <!-- Device event legend items (only show if present in current view) -->
      {#snippet sensorIcon()}<SensorIcon
          size={16}
          color="var(--glucose-in-range)"
        />{/snippet}
      {#snippet siteIcon()}<SiteChangeIcon
          size={16}
          color="var(--insulin-bolus)"
        />{/snippet}
      {#snippet reservoirIcon()}<ReservoirIcon
          size={16}
          color="var(--insulin-basal)"
        />{/snippet}
      {#snippet batteryIcon()}<BatteryIcon
          size={16}
          color="var(--carbs)"
        />{/snippet}
      {#if deviceEventMarkers.some((m) => m.eventType === "Sensor Start" || m.eventType === "Sensor Change")}
        {@render legendIndicator(sensorIcon, "Sensor")}
      {/if}
      {#if deviceEventMarkers.some((m) => m.eventType === "Site Change")}
        {@render legendIndicator(siteIcon, "Site")}
      {/if}
      {#if deviceEventMarkers.some((m) => m.eventType === "Insulin Change")}
        {@render legendIndicator(reservoirIcon, "Reservoir")}
      {/if}
      {#if deviceEventMarkers.some((m) => m.eventType === "Pump Battery Change")}
        {@render legendIndicator(batteryIcon, "Battery")}
      {/if}
      <!-- Pump mode toggle with expandable dropdown -->
      <div class="relative flex items-center">
        <!-- Visibility toggle button -->
        <button
          type="button"
          class={cn(
            "flex items-center gap-1 cursor-pointer hover:bg-accent/50 px-1.5 py-0.5 rounded-l transition-colors",
            !showPumpModes && "opacity-50"
          )}
          onclick={() => {
            showPumpModes = !showPumpModes;
            if (!showPumpModes) expandedPumpModes = false;
          }}
        >
          <PumpModeIcon
            state={currentPumpMode}
            size={14}
            class={showPumpModes ? "opacity-70" : "opacity-40"}
          />
          <span class={cn(!showPumpModes && "line-through")}>
            {currentPumpMode}
          </span>
        </button>
        <!-- Expand button (only show if multiple modes and visible) -->
        {#if uniquePumpModes.length > 1 && showPumpModes}
          <button
            type="button"
            class="flex items-center cursor-pointer hover:bg-accent/50 px-0.5 py-0.5 rounded-r transition-colors"
            onclick={() => (expandedPumpModes = !expandedPumpModes)}
          >
            <ChevronDown
              size={12}
              class={cn(
                "transition-transform",
                expandedPumpModes && "rotate-180"
              )}
            />
          </button>
        {/if}
        <!-- Expanded pump modes dropdown -->
        {#if expandedPumpModes && uniquePumpModes.length > 1}
          <div
            class="absolute top-full left-0 mt-1 bg-background border border-border rounded shadow-lg z-50 py-1 min-w-[120px]"
          >
            {#each uniquePumpModes as state}
              {@const span = pumpModeSpans.find((s) => s.state === state)}
              {#if span}
                <div
                  class="flex items-center gap-2 px-2 py-1 text-xs hover:bg-accent/50"
                >
                  <PumpModeIcon {state} size={14} color={span.color} />
                  <span>{state}</span>
                </div>
              {/if}
            {/each}
          </div>
        {/if}
      </div>
      <!-- System event legend items (only show if present in current view) - clickable to toggle -->
      {#if systemEvents.length > 0}
        {@const uniqueEventTypes = [
          ...new Set(systemEvents.map((e) => e.eventType)),
        ]}
        <button
          type="button"
          class={cn(
            "flex items-center gap-1 cursor-pointer hover:bg-accent/50 px-1.5 py-0.5 rounded transition-colors",
            !showAlarms && "opacity-50"
          )}
          onclick={() => (showAlarms = !showAlarms)}
        >
          {#each uniqueEventTypes.slice(0, 1) as eventType}
            {@const event = systemEvents.find((e) => e.eventType === eventType)}
            {#if event}
              <SystemEventIcon
                {eventType}
                size={14}
                color={showAlarms ? event.color : "var(--muted-foreground)"}
              />
            {/if}
          {/each}
          <span class={cn(!showAlarms && "line-through")}>
            Alarms ({systemEvents.length})
          </span>
        </button>
      {/if}
      <!-- Scheduled tracker legend items (only show if present in current view) - clickable to toggle -->
      {#if scheduledTrackerMarkers.length > 0}
        <button
          type="button"
          class={cn(
            "flex items-center gap-1 cursor-pointer hover:bg-accent/50 px-1.5 py-0.5 rounded transition-colors",
            !showScheduledTrackers && "opacity-50"
          )}
          onclick={() => (showScheduledTrackers = !showScheduledTrackers)}
        >
          <Clock
            size={14}
            class={showScheduledTrackers
              ? "text-primary"
              : "text-muted-foreground"}
          />
          <span class={cn(!showScheduledTrackers && "line-through")}>
            Scheduled ({scheduledTrackerMarkers.length})
          </span>
        </button>
      {/if}
      <!-- Override, Profile, Activity spans toggles -->
      {#snippet overrideIcon()}<div
          class="w-3 h-2 rounded border"
          style="background-color: var(--pump-mode-boost); opacity: 0.3; border-color: var(--pump-mode-boost)"
        ></div>{/snippet}
      {#snippet profileIcon()}<div
          class="w-3 h-2 rounded border"
          style="background-color: var(--chart-1); opacity: 0.2; border-color: var(--chart-1)"
        ></div>{/snippet}
      {#snippet activityIcon()}<div class="flex items-center gap-0.5">
          <div
            class="w-2 h-2 rounded"
            style="background-color: var(--pump-mode-sleep)"
          ></div>
          <div
            class="w-2 h-2 rounded"
            style="background-color: var(--pump-mode-exercise)"
          ></div>
        </div>{/snippet}
      {@render legendToggle(
        showOverrideSpans,
        () => (showOverrideSpans = !showOverrideSpans),
        "Overrides",
        overrideIcon
      )}
      {@render legendToggle(
        showProfileSpans,
        () => (showProfileSpans = !showProfileSpans),
        "Profile",
        profileIcon
      )}
      {@render legendToggle(
        showActivitySpans,
        () => (showActivitySpans = !showActivitySpans),
        "Activity",
        activityIcon
      )}
    </div>
  </CardContent>
</Card>

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

<!-- Disambiguation Dialog (when multiple treatments are nearby) -->
<Dialog.Root bind:open={isDisambiguationOpen}>
  <Dialog.Content class="max-w-md">
    <Dialog.Header>
      <Dialog.Title>Multiple Treatments</Dialog.Title>
      <Dialog.Description>
        Several treatments occurred around this time. Select one to edit.
      </Dialog.Description>
    </Dialog.Header>
    <div class="space-y-2 py-2">
      {#each nearbyTreatments as treatment (treatment._id || treatment.mills)}
        <button
          type="button"
          class="w-full flex items-center gap-3 p-3 rounded-lg bg-muted hover:bg-muted/80 transition-colors text-left"
          onclick={() => selectTreatmentFromList(treatment)}
        >
          <div class="flex-1">
            <div class="font-medium text-sm">
              {formatTreatmentSummary(treatment)}
            </div>
            <div class="text-xs text-muted-foreground">
              {treatment.created_at
                ? new Date(treatment.created_at).toLocaleTimeString([], {
                    hour: "numeric",
                    minute: "2-digit",
                  })
                : ""}
            </div>
          </div>
          <Badge variant="outline" class="text-xs">
            {treatment.eventType || "Unknown"}
          </Badge>
        </button>
      {/each}
    </div>
    <Dialog.Footer>
      <button
        type="button"
        class="px-4 py-2 text-sm rounded-md border border-input bg-background hover:bg-accent transition-colors"
        onclick={() => {
          isDisambiguationOpen = false;
          nearbyTreatments = [];
        }}
      >
        Cancel
      </button>
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>
