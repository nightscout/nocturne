<script lang="ts">
  import type { Entry, Treatment, DeviceStatus, TreatmentFood } from "$lib/api";

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
  import * as ToggleGroup from "$lib/components/ui/toggle-group";
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
  } from "layerchart";
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
    type TimeRangeOption,
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
  } from "$lib/components/icons";

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

  // Track if initial data has loaded to prevent effect loops during hydration
  let hasMounted = $state(false);
  $effect(() => {
    // Set mounted flag after first tick
    hasMounted = true;
  });

  // Fetch predictions when enabled (debounced by mounted state)
  $effect(() => {
    // Don't run until after initial mount to prevent loops during hydration
    if (!hasMounted) return;

    // Track dependencies - re-run when these change
    const enabled = predictionEnabled.current;
    const entryCount = entries.length;
    // Track the latest entry's timestamp to trigger refetch when new glucose arrives
    // This is important because entries.length doesn't change when array is capped
    const latestEntryMills = entries[0]?.mills ?? 0;

    // Only refetch if we have entries and predictions are enabled
    if (showPredictions && enabled && entryCount > 0 && latestEntryMills > 0) {
      getPredictions({})
        .then((data) => {
          predictionData = data;
          predictionError = null;
        })
        .catch((err) => {
          console.error("Failed to fetch predictions:", err);
          predictionError = err.message;
          predictionData = null;
        });
    }
  });

  // Round to minute boundaries to avoid triggering effects every second
  // Defined early because staleBasalData needs it
  const nowMinute = $derived(Math.floor(realtimeStore.now / 60000) * 60000);

  // Calculate most recent basal data source time
  // This is used to detect if the basal data is stale (e.g. from an external service like Glooko that hasn't synced recently)
  const lastBasalSourceTime = $derived.by(() => {
    // Check treatments for temp basals (which indicate active pump communication)
    const lastTempBasal = treatments.find((t) => t.eventType === "Temp Basal");
    const lastTempBasalTime =
      lastTempBasal != null
        ? (lastTempBasal.mills ?? 0) + (lastTempBasal.duration ?? 0)
        : 0;

    return lastTempBasalTime;
  });
  const STALE_THRESHOLD_MS = 10 * 60 * 1000; // 10 minutes

  const staleBasalData = $derived.by(() => {
    // If no data at all, nothing to mark
    if (lastBasalSourceTime === 0) return null;

    // Use nowMinute instead of Date.now() to prevent unstable re-computation
    const timeSinceLastUpdate = nowMinute - lastBasalSourceTime;

    if (timeSinceLastUpdate > STALE_THRESHOLD_MS) {
      return {
        start: new Date(lastBasalSourceTime),
        end: new Date(nowMinute), // Use stable time reference
      };
    }

    return null;
  });

  // Time range selection (in hours)

  // Time range selection (in hours)

  const timeRangeOptions: { value: TimeRangeOption; label: string }[] = [
    { value: "2", label: "2h" },
    { value: "4", label: "4h" },
    { value: "6", label: "6h" },
    { value: "12", label: "12h" },
    { value: "24", label: "24h" },
  ];

  function normalizeDate(
    date: Date | string | undefined,
    fallback: Date
  ): Date {
    if (!date) return fallback;
    return date instanceof Date ? date : new Date(date);
  }

  const displayDemoMode = $derived(demoMode ?? realtimeStore.demoMode);

  const displayDateRange = $derived({
    from: dateRange
      ? normalizeDate(dateRange.from, new Date())
      : new Date(
          nowMinute - parseInt(glucoseChartLookback.current) * 60 * 60 * 1000
        ),
    to: dateRange
      ? normalizeDate(dateRange.to, new Date())
      : new Date(nowMinute),
  });

  // Fetch server-side chart data when date range changes (guarded by hasMounted)
  $effect(() => {
    // Don't run until after initial mount to prevent loops during hydration
    if (!hasMounted) return;

    const startTime = displayDateRange.from.getTime();
    const endTime = displayDateRange.to.getTime();

    if (isNaN(startTime) || isNaN(endTime)) return;

    getChartData({ startTime, endTime, intervalMinutes: 5 })
      .then((data) => {
        serverChartData = data;
      })
      .catch((err) => {
        console.error("Failed to fetch chart data:", err);
        serverChartData = null;
      });

    // Also fetch state span data
    getChartStateData({ startTime, endTime })
      .then((data) => {
        stateData = data;
      })
      .catch((err) => {
        console.error("Failed to fetch state data:", err);
        stateData = null;
      });
  });

  // Prediction buffer
  const predictionHours = $derived(predictionMinutes.current / 60);
  const chartXDomain = $derived({
    from: displayDateRange.from,
    to:
      showPredictions && predictionData
        ? new Date(
            displayDateRange.to.getTime() + predictionHours * 60 * 60 * 1000
          )
        : displayDateRange.to,
  });

  // Filter entries by date range
  const filteredEntries = $derived(
    entries.filter((e) => {
      const entryTime = e.mills ?? 0;
      return (
        entryTime >= displayDateRange.from.getTime() &&
        entryTime <= displayDateRange.to.getTime()
      );
    })
  );

  // Filter treatments by date range
  const filteredTreatments = $derived(
    treatments.filter((t) => {
      const treatmentTime =
        t.mills ?? (t.created_at ? new Date(t.created_at).getTime() : 0);
      return (
        treatmentTime >= displayDateRange.from.getTime() &&
        treatmentTime <= displayDateRange.to.getTime()
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
  // Helper function to compute track ratios based on visibility
  // Using a function instead of $derived to avoid reactivity loops
  function getTrackRatios() {
    const showBasalTrack = showBasal;
    const showIobTrack = showIob || showCob;
    const basalRatio = showBasalTrack ? 0.12 : 0;
    const iobRatio = showIobTrack ? 0.18 : 0;
    // Glucose gets the remaining space (base 0.7 + any hidden track space)
    const glucoseRatio = 1 - basalRatio - iobRatio;
    return {
      basal: basalRatio,
      glucose: glucoseRatio,
      iob: iobRatio,
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

  // Find active pump mode span at a given time
  function findActivePumpMode(time: Date) {
    const timeMs = time.getTime();
    return pumpModeSpans.find((span) => {
      const spanStart = span.startTime.getTime();
      const spanEnd = span.endTime?.getTime() ?? Date.now();
      return timeMs >= spanStart && timeMs <= spanEnd;
    });
  }

  // Find nearby system event
  function findNearbySystemEvent(time: Date) {
    return systemEvents.find(
      (event) =>
        Math.abs(event.time.getTime() - time.getTime()) < TREATMENT_PROXIMITY_MS
    );
  }
</script>

<Card class="bg-card border-border">
  <CardHeader class="pb-2">
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
          bind:predictionMode={predictionDisplayMode.current}
        />
        <!-- Time range selector -->
        <ToggleGroup.Root
          type="single"
          bind:value={glucoseChartLookback.current}
          class="bg-muted rounded-lg p-0.5"
        >
          {#each timeRangeOptions as option}
            <ToggleGroup.Item
              value={option.value}
              class="px-3 py-1 text-xs font-medium text-muted-foreground data-[state=on]:bg-accent data-[state=on]:text-accent-foreground rounded-md transition-colors"
            >
              {option.label}
            </ToggleGroup.Item>
          {/each}
        </ToggleGroup.Root>
      </div>
    </div>
  </CardHeader>

  <CardContent class="p-2">
    <!-- Single compound chart with remapped scales for basal, glucose, and IOB -->
    <div class="h-[450px] p-4">
      <Chart
        data={glucoseData}
        x={(d) => d.time}
        y="sgv"
        xScale={scaleTime()}
        xDomain={[chartXDomain.from, chartXDomain.to]}
        yDomain={[0, glucoseYMax]}
        padding={{ left: 48, bottom: 30, top: 8, right: 48 }}
        tooltip={{ mode: "quadtree-x" }}
        brush
      >
        {#snippet children({ context })}
          <Svg>
            <!-- Get track configuration (ratios and visibility flags) -->
            {@const trackConfig = getTrackRatios()}
            {@const { showBasalTrack, showIobTrack } = trackConfig}

            <!-- Create remapped scales for basal, glucose, and IOB tracks -->
            <!-- Layout from top to bottom: BASAL | GLUCOSE | IOB -->
            {@const basalTrackHeight = context.height * trackConfig.basal}
            {@const glucoseTrackHeight = context.height * trackConfig.glucose}
            {@const iobTrackHeight = context.height * trackConfig.iob}

            <!-- Track positions (y coordinates in SVG where 0 = top) -->
            {@const basalTrackTop = 0}
            {@const basalTrackBottom = basalTrackHeight}
            {@const glucoseTrackTop = basalTrackBottom}
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
            <!-- ===== BASAL TRACK (TOP) ===== -->
            <!-- Pump mode background bands (render first, behind everything else) -->
            {#each pumpModeSpans as span (span.id)}
              {@const spanXPos = context.xScale(span.displayStart)}
              <AnnotationRange
                x={[span.displayStart.getTime(), span.displayEnd.getTime()]}
                y={[basalScale(maxBasalRate), basalZero]}
                fill={span.color}
                class="opacity-20"
              />
              <!-- Pump mode icon at the start of each span -->
              <Group
                x={spanXPos}
                y={context.yScale(basalScale(maxBasalRate) + 6)}
              >
                <foreignObject x="2" y="-8" width="16" height="16">
                  <div class="flex items-center justify-center w-full h-full">
                    <PumpModeIcon
                      state={span.state}
                      size={12}
                      color={span.color}
                    />
                  </div>
                </foreignObject>
              </Group>
            {/each}

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

            <!-- X-Axis (bottom) -->
            <Axis
              placement="bottom"
              format={(v) => (v instanceof Date ? formatTime(v) : String(v))}
              tickLabelProps={{ class: "text-xs fill-muted-foreground" }}
            />

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
                    <div class="flex items-center justify-center w-full h-full">
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
                {@const yPos = context.yScale(glucoseScale(lowThreshold * 0.8))}
                <Group x={xPos} y={yPos}>
                  <!-- Icon using foreignObject to embed Lucide component -->
                  <foreignObject x="-8" y="-8" width="16" height="16">
                    <div class="flex items-center justify-center w-full h-full">
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
          </Svg>

          <Tooltip.Root
            {context}
            class="bg-popover/95 border border-border rounded-lg shadow-xl text-xs z-50 backdrop-blur-sm"
          >
            {#snippet children({ data })}
              {@const activeBasal = findBasalValue(basalData, data.time)}
              {@const activeIob = findSeriesValue(iobData, data.time)}
              {@const activeCob = findSeriesValue(cobData, data.time)}
              {@const nearbyBolus = findNearbyBolus(data.time)}
              {@const nearbyCarbs = findNearbyCarbs(data.time)}
              {@const nearbyDeviceEvent = findNearbyDeviceEvent(data.time)}
              {@const activePumpMode = findActivePumpMode(data.time)}
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
                {#if nearbyBolus}
                  <Tooltip.Item
                    label="Bolus"
                    value={`${nearbyBolus.insulin.toFixed(1)}U`}
                    color="var(--insulin-bolus)"
                    class="font-medium"
                  />
                {/if}
                {#if nearbyCarbs}
                  <Tooltip.Item
                    label="Carbs"
                    value={`${nearbyCarbs.carbs}g`}
                    color="var(--carbs)"
                    class="font-medium"
                  />
                {/if}
                {#if nearbyDeviceEvent}
                  <Tooltip.Item
                    label={nearbyDeviceEvent.eventType}
                    value={nearbyDeviceEvent.notes || ""}
                    color={nearbyDeviceEvent.config.color}
                    class="font-medium"
                  />
                {/if}
                {#if activeIob}
                  <Tooltip.Item
                    label="IOB"
                    value={activeIob.value}
                    format={"decimal"}
                    color="var(--iob-basal)"
                  />
                {/if}
                {#if activeCob && activeCob.value > 0}
                  <Tooltip.Item
                    label="COB"
                    value={`${activeCob.value.toFixed(0)}g`}
                    color="var(--carbs)"
                  />
                {/if}
                {#if activeBasal}
                  <Tooltip.Item
                    label={activeBasal.isTemp ? "Temp Basal" : "Basal"}
                    value={activeBasal.rate}
                    format={"decimal"}
                    color={activeBasal.isTemp
                      ? "var(--insulin-temp-basal)"
                      : "var(--insulin-basal)"}
                    class={cn(
                      staleBasalData && data.time >= staleBasalData.start
                        ? "text-yellow-500 font-bold"
                        : ""
                    )}
                  />
                  {#if activeBasal.isTemp && activeBasal.scheduledRate !== undefined}
                    <Tooltip.Item
                      label="Scheduled"
                      value={activeBasal.scheduledRate}
                      format={"decimal"}
                      color="var(--muted-foreground)"
                    />
                  {/if}
                {/if}
                {#if activePumpMode}
                  <Tooltip.Item
                    label="Pump Mode"
                    value={activePumpMode.state}
                    color={activePumpMode.color}
                    class="font-medium"
                  />
                {/if}
                {#if nearbySystemEvent}
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

    <!-- Legend -->
    <div
      class="flex flex-wrap justify-center gap-4 text-sm text-muted-foreground pt-2"
    >
      <div class="flex items-center gap-1">
        <div class="w-2 h-2 rounded-full bg-glucose-in-range"></div>
        <span>In Range</span>
      </div>
      <!-- only show if very high values are present -->
      {#if glucoseData.some((d) => d.sgv > veryHighThreshold)}
        <div class="flex items-center gap-1">
          <div class="w-2 h-2 rounded-full bg-glucose-very-high"></div>
          <span>Very High</span>
        </div>
      {/if}
      {#if glucoseData.some((d) => d.sgv > highThreshold && d.sgv <= veryHighThreshold)}
        <div class="flex items-center gap-1">
          <div class="w-2 h-2 rounded-full bg-glucose-high"></div>
          <span>High</span>
        </div>
      {/if}
      {#if glucoseData.some((d) => d.sgv < lowThreshold && d.sgv >= veryLowThreshold)}
        <div class="flex items-center gap-1">
          <div class="w-2 h-2 rounded-full bg-glucose-low"></div>
          <span>Low</span>
        </div>
      {/if}
      {#if glucoseData.some((d) => d.sgv < veryLowThreshold)}
        <div class="flex items-center gap-1">
          <div class="w-2 h-2 rounded-full bg-glucose-very-low"></div>
          <span>Very Low</span>
        </div>
      {/if}
      <button
        type="button"
        class={cn(
          "flex items-center gap-1 cursor-pointer hover:bg-accent/50 px-1.5 py-0.5 rounded transition-colors",
          !showBasal && "opacity-50"
        )}
        onclick={() => (showBasal = !showBasal)}
      >
        <div class="w-3 h-2 bg-insulin-basal border border-insulin"></div>
        <span class={cn(!showBasal && "line-through")}>Basal</span>
      </button>
      <button
        type="button"
        class={cn(
          "flex items-center gap-1 cursor-pointer hover:bg-accent/50 px-1.5 py-0.5 rounded transition-colors",
          !showIob && "opacity-50"
        )}
        onclick={() => (showIob = !showIob)}
      >
        <div class="w-3 h-2 bg-iob-basal border border-insulin"></div>
        <span class={cn(!showIob && "line-through")}>IOB</span>
      </button>
      <button
        type="button"
        class={cn(
          "flex items-center gap-1 cursor-pointer hover:bg-accent/50 px-1.5 py-0.5 rounded transition-colors",
          !showCob && "opacity-50"
        )}
        onclick={() => (showCob = !showCob)}
      >
        <div class="w-3 h-2 bg-carbs/40 border border-carbs"></div>
        <span class={cn(!showCob && "line-through")}>COB</span>
      </button>
      <button
        type="button"
        class={cn(
          "flex items-center gap-1 cursor-pointer hover:bg-accent/50 px-1.5 py-0.5 rounded transition-colors",
          !showBolus && "opacity-50"
        )}
        onclick={() => (showBolus = !showBolus)}
      >
        <BolusIcon size={16} />
        <span class={cn(!showBolus && "line-through")}>Bolus</span>
      </button>
      <button
        type="button"
        class={cn(
          "flex items-center gap-1 cursor-pointer hover:bg-accent/50 px-1.5 py-0.5 rounded transition-colors",
          !showCarbs && "opacity-50"
        )}
        onclick={() => (showCarbs = !showCarbs)}
      >
        <CarbsIcon size={16} />
        <span class={cn(!showCarbs && "line-through")}>Carbs</span>
      </button>
      <!-- Device event legend items (only show if present in current view) -->
      {#if deviceEventMarkers.some((m) => m.eventType === "Sensor Start" || m.eventType === "Sensor Change")}
        <div class="flex items-center gap-1">
          <SensorIcon size={16} color="var(--glucose-in-range)" />
          <span>Sensor</span>
        </div>
      {/if}
      {#if deviceEventMarkers.some((m) => m.eventType === "Site Change")}
        <div class="flex items-center gap-1">
          <SiteChangeIcon size={16} color="var(--insulin-bolus)" />
          <span>Site</span>
        </div>
      {/if}
      {#if deviceEventMarkers.some((m) => m.eventType === "Insulin Change")}
        <div class="flex items-center gap-1">
          <ReservoirIcon size={16} color="var(--insulin-basal)" />
          <span>Reservoir</span>
        </div>
      {/if}
      {#if deviceEventMarkers.some((m) => m.eventType === "Pump Battery Change")}
        <div class="flex items-center gap-1">
          <BatteryIcon size={16} color="var(--carbs)" />
          <span>Battery</span>
        </div>
      {/if}
      <!-- Pump mode legend items (only show if present in current view) -->
      {#each [...new Set(pumpModeSpans.map((s) => s.state))] as state}
        {@const span = pumpModeSpans.find((s) => s.state === state)}
        {#if span}
          <div class="flex items-center gap-1">
            <PumpModeIcon
              {state}
              size={14}
              class="opacity-70"
              color={span.color}
            />
            <span>{state}</span>
          </div>
        {/if}
      {/each}
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
