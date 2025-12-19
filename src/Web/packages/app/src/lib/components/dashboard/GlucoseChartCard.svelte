<script lang="ts">
  import type { Entry, Treatment, DeviceStatus } from "$lib/api";
  import {
    Card,
    CardContent,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { Badge } from "$lib/components/ui/badge";
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
    Rule,
    Points,
    Highlight,
    Text,
    Tooltip,
    AnnotationRange,
    ChartClipPath,
  } from "layerchart";
  import { chartConfig } from "$lib/constants";
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
    predictionMinutes,
    predictionEnabled,
  } from "$lib/stores/appearance-store.svelte";
  import { bg } from "$lib/utils/formatting";
  import PredictionSettings from "./PredictionSettings.svelte";
  import { cn } from "$lib/utils";
  import { goto } from "$app/navigation";

  interface ComponentProps {
    entries?: Entry[];
    treatments?: Treatment[];
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
    /** Prediction model from algorithm settings (ar2, linear, iob, cob, uam) */
    predictionModel?: string;
  }

  const realtimeStore = getRealtimeStore();
  let {
    entries = realtimeStore.entries,
    treatments = realtimeStore.treatments,
    deviceStatuses = realtimeStore.deviceStatuses,
    demoMode = realtimeStore.demoMode,
    dateRange,
    defaultBasalRate = 1.0,
    carbRatio = 15,
    isf = 50,
    showPredictions = true,
    defaultFocusHours,
    predictionModel = "cone",
  }: ComponentProps = $props();

  // Prediction data state
  let predictionData = $state<PredictionData | null>(null);
  let predictionError = $state<string | null>(null);

  // Server-side chart data (IOB, COB, basal)
  let serverChartData = $state<DashboardChartData | null>(null);

  // Prediction display mode
  type PredictionDisplayMode =
    | "cone"
    | "lines"
    | "main"
    | "iob"
    | "zt"
    | "uam"
    | "cob";
  // Sync prediction mode with algorithm settings model
  const modelToMode: Record<string, PredictionDisplayMode> = {
    ar2: "cone",
    linear: "cone",
    iob: "iob",
    cob: "cob",
    uam: "uam",
    cone: "cone",
    lines: "lines",
  };

  let predictionMode = $state<PredictionDisplayMode>(
    modelToMode[predictionModel] ?? "cone"
  );

  // Suppress unused variable warnings
  void isf;
  void carbRatio;

  // Fetch predictions when enabled
  $effect(() => {
    // Track dependencies - re-run when these change
    const enabled = predictionEnabled.current;

    // Explicitly track the last updated timestamp from the store to know when new data arrives
    void realtimeStore.lastUpdated;

    if (showPredictions && enabled && entries.length > 0) {
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

    const timeSinceLastUpdate = Date.now() - lastBasalSourceTime;

    if (timeSinceLastUpdate > STALE_THRESHOLD_MS) {
      return {
        start: new Date(lastBasalSourceTime),
        end: new Date(), // Up to now
      };
    }

    return null;
  });

  // Time range selection (in hours)
  type TimeRangeOption = "2" | "4" | "6" | "12" | "24";

  function getInitialTimeRange(hours?: number): TimeRangeOption {
    const validOptions: TimeRangeOption[] = ["2", "4", "6", "12", "24"];
    const hourStr = String(hours) as TimeRangeOption;
    return validOptions.includes(hourStr) ? hourStr : "6";
  }

  let selectedTimeRange = $state<TimeRangeOption>(
    getInitialTimeRange(defaultFocusHours)
  );

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
          realtimeStore.now - parseInt(selectedTimeRange) * 60 * 60 * 1000
        ),
    to: dateRange
      ? normalizeDate(dateRange.to, new Date())
      : new Date(realtimeStore.now),
  });

  // Fetch server-side chart data when date range changes
  $effect(() => {
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

  function getTreatmentTime(t: Treatment): number {
    return t.mills ?? (t.created_at ? new Date(t.created_at).getTime() : 0);
  }

  // Thresholds (convert to display units)
  const lowThreshold = $derived(Number(bg(chartConfig.low.threshold ?? 55)));
  const highThreshold = $derived(Number(bg(chartConfig.high.threshold ?? 180)));

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

  // Prediction curve data (filtered to prediction window)
  const predictionEndTime = $derived(chartXDomain.to.getTime());

  const predictionCurveData = $derived(
    predictionData?.curves.main
      .filter((p) => p.timestamp <= predictionEndTime)
      .map((p) => ({
        time: new Date(p.timestamp),
        sgv: bg(p.value),
      })) ?? []
  );

  const iobPredictionData = $derived(
    predictionData?.curves.iobOnly
      .filter((p) => p.timestamp <= predictionEndTime)
      .map((p) => ({
        time: new Date(p.timestamp),
        sgv: bg(p.value),
      })) ?? []
  );

  const uamPredictionData = $derived(
    predictionData?.curves.uam
      .filter((p) => p.timestamp <= predictionEndTime)
      .map((p) => ({
        time: new Date(p.timestamp),
        sgv: bg(p.value),
      })) ?? []
  );

  const cobPredictionData = $derived(
    predictionData?.curves.cob
      .filter((p) => p.timestamp <= predictionEndTime)
      .map((p) => ({
        time: new Date(p.timestamp),
        sgv: bg(p.value),
      })) ?? []
  );

  const zeroTempPredictionData = $derived(
    predictionData?.curves.zeroTemp
      .filter((p) => p.timestamp <= predictionEndTime)
      .map((p) => ({
        time: new Date(p.timestamp),
        sgv: bg(p.value),
      })) ?? []
  );

  // Prediction cone data (filtered to prediction window)
  const predictionConeData = $derived.by(() => {
    if (!predictionData) return [];

    const curves = [
      predictionData.curves.main,
      predictionData.curves.iobOnly,
      predictionData.curves.zeroTemp,
      predictionData.curves.uam,
      predictionData.curves.cob,
    ].filter((c) => c && c.length > 0);

    if (curves.length === 0) return [];

    const primaryCurve = curves[0];
    return primaryCurve
      .filter((point) => point.timestamp <= predictionEndTime)
      .map((point, i) => {
        const valuesAtTime = curves.map((c) => c[i]?.value ?? point.value);
        return {
          time: new Date(point.timestamp),
          min: bg(Math.min(...valuesAtTime)),
          max: bg(Math.max(...valuesAtTime)),
          mid: bg((Math.min(...valuesAtTime) + Math.max(...valuesAtTime)) / 2),
        };
      });
  });

  // Use server-side data for IOB and basal, with fallbacks
  const iobData = $derived(serverChartData?.iobSeries ?? []);
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
  // Track proportion ratios (configurable) - must sum to 1.0
  const trackRatios = {
    basal: 0.12, // 12% of chart height
    glucose: 0.7, // 70% of chart height
    iob: 0.18, // 18% of chart height
  };

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

  // Treatment marker data for IOB track
  const bolusMarkersForIob = $derived(
    bolusTreatments.map((t) => ({
      time: new Date(getTreatmentTime(t)),
      insulin: t.insulin ?? 0,
    }))
  );

  const carbMarkersForIob = $derived(
    carbTreatments.map((t) => ({
      time: new Date(getTreatmentTime(t)),
      carbs: t.carbs ?? 0,
    }))
  );

  // Find treatments near a given time (within 5 minute window)
  const TREATMENT_PROXIMITY_MS = 5 * 60 * 1000;

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
</script>

<Card class="bg-slate-950 border-slate-800">
  <CardHeader class="pb-2">
    <div class="flex items-center justify-between flex-wrap gap-2">
      <CardTitle class="flex items-center gap-2 text-slate-100">
        Blood Glucose
        {#if displayDemoMode}
          <Badge
            variant="outline"
            class="text-xs border-slate-700 text-slate-400"
          >
            Demo
          </Badge>
        {/if}
      </CardTitle>

      <div class="flex items-center gap-2">
        <!-- Prediction settings component with its own boundary -->
        <PredictionSettings
          {showPredictions}
          bind:predictionMode
          {predictionModel}
        />
        <!-- Time range selector -->
        <ToggleGroup.Root
          type="single"
          bind:value={selectedTimeRange}
          class="bg-slate-900 rounded-lg p-0.5"
        >
          {#each timeRangeOptions as option}
            <ToggleGroup.Item
              value={option.value}
              class="px-3 py-1 text-xs font-medium text-slate-400 data-[state=on]:bg-slate-700 data-[state=on]:text-slate-100 rounded-md transition-colors"
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
      >
        {#snippet children({ context })}
          <Svg>
            <!-- Create remapped scales for basal, glucose, and IOB tracks -->
            <!-- Layout from top to bottom: BASAL | GLUCOSE | IOB -->
            {@const basalTrackHeight = context.height * trackRatios.basal}
            {@const glucoseTrackHeight = context.height * trackRatios.glucose}
            {@const iobTrackHeight = context.height * trackRatios.iob}

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
              <Rule
                x={staleBasalData.start}
                class="stroke-yellow-500/50 stroke-1"
                stroke-dasharray="2,2"
              />
            {/if}

            <!-- Scheduled basal rate line -->
            {#if scheduledBasalData.length > 0}
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

            <!-- Effective basal area (drips down from top of basal track) -->
            <!-- y0 = baseline (0 rate at top), y1 = actual rate (grows down) -->
            {#if basalData.length > 0}
              <Area
                data={basalData}
                x={(d) => d.time}
                y0={() => basalZero}
                y1={(d) => basalScale(d.rate)}
                curve={curveStepAfter}
                fill="var(--insulin-basal)"
                class="stroke-insulin stroke-1"
              />
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

            <!-- Glucose points -->
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

            <!-- Prediction visualizations -->
            <svelte:boundary>
              {#snippet pending()}
                <Spline
                  data={[
                    {
                      time: chartXDomain.to,
                      sgv: glucoseData.at(-1)?.sgv ?? 100,
                    },
                    {
                      time: new Date(
                        chartXDomain.to.getTime() + 30 * 60 * 1000
                      ),
                      sgv: glucoseData.at(-1)?.sgv ?? 100,
                    },
                  ]}
                  x={(d) => d.time}
                  y={(d) => glucoseScale(d.sgv)}
                  curve={curveMonotoneX}
                  class="stroke-slate-500/50 stroke-1 fill-none animate-pulse"
                  stroke-dasharray="4,4"
                />
                <Text
                  x={chartXDomain.to.getTime() + 5 * 60 * 1000}
                  y={glucoseScale(Number(glucoseData.at(-1)?.sgv) ?? 100)}
                  class="text-[9px] fill-slate-500 animate-pulse"
                >
                  Loading predictions...
                </Text>
              {/snippet}

              {#snippet failed(error)}
                <Text
                  x={50}
                  y={glucoseTrackTop + 20}
                  class="text-xs fill-red-400"
                >
                  Prediction unavailable: {error instanceof Error
                    ? error.message
                    : "Error"}
                </Text>
              {/snippet}

              {#if showPredictions && predictionEnabled.current && predictionData}
                {#if predictionMode === "cone" && predictionConeData.length > 0}
                  <Area
                    data={predictionConeData}
                    x={(d) => d.time}
                    y0={(d) => glucoseScale(d.max)}
                    y1={(d) => glucoseScale(d.min)}
                    curve={curveMonotoneX}
                    class="fill-purple-500/20 stroke-none"
                    motion="spring"
                  />
                  <Spline
                    data={predictionConeData}
                    x={(d) => d.time}
                    y={(d) => glucoseScale(d.mid)}
                    curve={curveMonotoneX}
                    motion="spring"
                    class="stroke-purple-400 stroke-1 fill-none"
                    stroke-dasharray="4,2"
                  />
                {:else if predictionMode === "lines"}
                  {#if predictionCurveData.length > 0}
                    <Spline
                      data={predictionCurveData}
                      x={(d) => d.time}
                      y={(d) => glucoseScale(d.sgv)}
                      curve={curveMonotoneX}
                      motion="spring"
                      class="stroke-purple-400 stroke-2 fill-none"
                      stroke-dasharray="6,3"
                    />
                  {/if}
                  {#if iobPredictionData.length > 0}
                    <Spline
                      data={iobPredictionData}
                      x={(d) => d.time}
                      y={(d) => glucoseScale(d.sgv)}
                      curve={curveMonotoneX}
                      motion="spring"
                      class="stroke-cyan-400 stroke-1 fill-none opacity-80"
                      stroke-dasharray="4,2"
                    />
                  {/if}
                  {#if zeroTempPredictionData.length > 0}
                    <Spline
                      data={zeroTempPredictionData}
                      x={(d) => d.time}
                      y={(d) => glucoseScale(d.sgv)}
                      curve={curveMonotoneX}
                      motion="spring"
                      class="stroke-orange-400 stroke-1 fill-none opacity-80"
                      stroke-dasharray="4,2"
                    />
                  {/if}
                  {#if uamPredictionData.length > 0}
                    <Spline
                      data={uamPredictionData}
                      x={(d) => d.time}
                      y={(d) => glucoseScale(d.sgv)}
                      curve={curveMonotoneX}
                      motion="spring"
                      class="stroke-green-400 stroke-1 fill-none opacity-80"
                      stroke-dasharray="4,2"
                    />
                  {/if}
                  {#if cobPredictionData.length > 0}
                    <Spline
                      data={cobPredictionData}
                      x={(d) => d.time}
                      y={(d) => glucoseScale(d.sgv)}
                      motion="spring"
                      curve={curveMonotoneX}
                      class="stroke-yellow-400 stroke-1 fill-none opacity-80"
                      stroke-dasharray="4,2"
                    />
                  {/if}
                {:else if predictionMode === "main" && predictionCurveData.length > 0}
                  <Spline
                    data={predictionCurveData}
                    x={(d) => d.time}
                    y={(d) => glucoseScale(d.sgv)}
                    motion="spring"
                    curve={curveMonotoneX}
                    class="stroke-purple-400 stroke-2 fill-none"
                    stroke-dasharray="6,3"
                  />
                {:else if predictionMode === "iob" && iobPredictionData.length > 0}
                  <Spline
                    data={iobPredictionData}
                    x={(d) => d.time}
                    y={(d) => glucoseScale(d.sgv)}
                    motion="spring"
                    curve={curveMonotoneX}
                    class="stroke-cyan-400 stroke-2 fill-none"
                    stroke-dasharray="6,3"
                  />
                {:else if predictionMode === "zt" && zeroTempPredictionData.length > 0}
                  <Spline
                    data={zeroTempPredictionData}
                    x={(d) => d.time}
                    y={(d) => glucoseScale(d.sgv)}
                    motion="spring"
                    curve={curveMonotoneX}
                    class="stroke-orange-400 stroke-2 fill-none"
                    stroke-dasharray="6,3"
                  />
                {:else if predictionMode === "uam" && uamPredictionData.length > 0}
                  <Spline
                    data={uamPredictionData}
                    x={(d) => d.time}
                    y={(d) => glucoseScale(d.sgv)}
                    motion="spring"
                    curve={curveMonotoneX}
                    class="stroke-green-400 stroke-2 fill-none"
                    stroke-dasharray="6,3"
                  />
                {:else if predictionMode === "cob" && cobPredictionData.length > 0}
                  <Spline
                    data={cobPredictionData}
                    x={(d) => d.time}
                    y={(d) => glucoseScale(d.sgv)}
                    motion="spring"
                    curve={curveMonotoneX}
                    class="stroke-yellow-400 stroke-2 fill-none"
                    stroke-dasharray="6,3"
                  />
                {/if}
              {/if}
              {#if showPredictions && predictionError}
                <Text
                  x={50}
                  y={glucoseTrackTop + 20}
                  class="text-xs fill-red-400"
                >
                  Prediction unavailable
                </Text>
              {/if}
            </svelte:boundary>

            <!-- ===== IOB TRACK (BOTTOM) with Treatment Markers ===== -->
            <!-- IOB axis on right -->
            <Axis
              placement="right"
              scale={iobAxisScale}
              ticks={2}
              tickLabelProps={{ class: "text-[9px] fill-muted-foreground" }}
            />

            <!-- IOB track label -->
            <Text
              x={4}
              y={iobTrackTop + 12}
              class="text-[8px] fill-muted-foreground font-medium"
            >
              IOB
            </Text>

            <!-- IOB area (grows up from bottom of IOB track) -->
            {#if iobData.length > 0 && iobData.some((d) => d.value > 0.01)}
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

            <!-- Bolus markers (on top layer) -->
            {#each bolusMarkersForIob as marker}
              {@const xPos = context.xScale(marker.time)}
              {@const yPos = context.yScale(iobScale(marker.insulin))}
              <Group x={xPos} y={yPos + 0}>
                <Polygon
                  points={[
                    { x: 0, y: 10 },
                    { x: -5, y: 0 },
                    { x: 5, y: 0 },
                  ]}
                  class="opacity-90 fill-insulin-bolus"
                />
                <Text
                  y={-14}
                  textAnchor="middle"
                  class="text-[8px] fill-insulin-bolus font-medium"
                >
                  {marker.insulin.toFixed(1)}U
                </Text>
              </Group>
            {/each}

            <!-- Carb markers (on top layer) -->
            {#each carbMarkersForIob as marker}
              {@const xPos = context.xScale(marker.time)}
              {@const yPos = context.yScale(iobScale(marker.carbs / carbRatio))}
              <Group x={xPos} y={yPos}>
                <Polygon
                  points={[
                    { x: 0, y: -10 },
                    { x: -5, y: 0 },
                    { x: 5, y: 0 },
                  ]}
                  fill="var(--carbs)"
                  class="opacity-90"
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
            <!-- Basal highlight with remapped scale -->
            <Highlight
              x={(d) => d.time}
              y={(d) => {
                const basal = findBasalValue(basalData, d.time);
                return basalScale(basal?.rate ?? 0);
              }}
              points={{ class: "fill-insulin-basal" }}
            />

            <!-- IOB highlight with remapped scale -->
            <Highlight
              x={(d) => d.time}
              y={(d) => {
                const iob = findSeriesValue(iobData, d.time);
                if (!iob || iob.value <= 0) return null;
                return iobScale(iob.value);
              }}
              points={{ class: "fill-iob-basal" }}
            />
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
            class="bg-slate-900/95 border border-slate-800 rounded-lg shadow-xl text-xs z-50 backdrop-blur-sm"
          >
            {#snippet children({ data })}
              {@const activeBasal = findBasalValue(basalData, data.time)}
              {@const activeIob = findSeriesValue(iobData, data.time)}
              {@const nearbyBolus = findNearbyBolus(data.time)}
              {@const nearbyCarbs = findNearbyCarbs(data.time)}

              <Tooltip.Header
                value={data?.time}
                format="minute"
                class="text-slate-100 border-b border-slate-800 pb-1 mb-1 text-sm font-semibold"
              />
              <Tooltip.List>
                {#if data?.sgv}
                  <Tooltip.Item
                    label="Glucose"
                    value={data.sgv}
                    format="integer"
                    color="var(--glucose-in-range)"
                    class="text-slate-100 font-bold"
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
                {#if activeIob}
                  <Tooltip.Item
                    label="IOB"
                    value={activeIob.value}
                    format={"decimal"}
                    color="var(--iob-basal)"
                  />
                {/if}
                {#if activeBasal}
                  <Tooltip.Item
                    label={activeBasal.isTemp ? "Temp Basal" : "Basal"}
                    value={activeBasal.rate}
                    format={"decimal"}
                    color="var(--insulin-basal)"
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
              </Tooltip.List>
            {/snippet}
          </Tooltip.Root>
          <Tooltip.Root
            x="data"
            y={context.height + context.padding.top}
            yOffset={2}
            anchor="top"
            variant="none"
            class="text-sm font-semibold leading-3 px-2 py-1 rounded-sm whitespace-nowrap"
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
      class="flex flex-wrap justify-center gap-4 text-[10px] text-muted-foreground pt-2"
    >
      <div class="flex items-center gap-1">
        <div
          class="w-2 h-2 rounded-full"
          style="background: var(--glucose-in-range)"
        ></div>
        <span>In Range</span>
      </div>
      <div class="flex items-center gap-1">
        <div
          class="w-2 h-2 rounded-full"
          style="background: var(--glucose-high)"
        ></div>
        <span>High</span>
      </div>
      <div class="flex items-center gap-1">
        <div
          class="w-2 h-2 rounded-full"
          style="background: var(--glucose-very-low)"
        ></div>
        <span>Low</span>
      </div>
      <div class="flex items-center gap-1">
        <div
          class="w-3 h-2"
          style="background: var(--insulin-basal); border: 1px solid var(--insulin)"
        ></div>
        <span>Basal</span>
      </div>
      <div class="flex items-center gap-1">
        <div
          class="w-3 h-2"
          style="background: var(--iob-basal); border: 1px solid var(--insulin)"
        ></div>
        <span>IOB</span>
      </div>
      <div class="flex items-center gap-1">
        <div
          class="w-0 h-0 border-l-4 border-r-4 border-b-4 border-l-transparent border-r-transparent"
          style="border-bottom-color: var(--insulin-bolus)"
        ></div>
        <span>Bolus</span>
      </div>
      <div class="flex items-center gap-1">
        <div
          class="w-0 h-0 border-l-4 border-r-4 border-t-4 border-l-transparent border-r-transparent"
          style="border-top-color: var(--carbs)"
        ></div>
        <span>Carbs</span>
      </div>
    </div>
  </CardContent>
</Card>
