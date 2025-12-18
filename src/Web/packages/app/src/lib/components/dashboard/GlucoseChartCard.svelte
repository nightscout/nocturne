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
  import { scaleTime } from "d3-scale";
  import {
    getPredictions,
    type PredictionData,
  } from "$lib/data/predictions.remote";
  import {
    getChartData,
    type DashboardChartData,
  } from "$lib/data/chart-data.remote";
  import {
    glucoseUnits,
    predictionMinutes,
    predictionEnabled,
  } from "$lib/stores/appearance-store.svelte";
  import { convertToDisplayUnits } from "$lib/utils/formatting";
  import PredictionSettings from "./PredictionSettings.svelte";
  import { cn } from "$lib/utils";

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
    carbRatio = 10,
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
    // Check device statuses for pump status updates
    const lastDeviceStatus = deviceStatuses[0];
    const lastDeviceStatusTime = lastDeviceStatus?.mills ?? 0;

    // Check treatments for temp basals (which indicate active pump communication)
    const lastTempBasal = treatments.find((t) => t.eventType === "Temp Basal");
    const lastTempBasalTime = lastTempBasal?.mills ?? 0;

    // Also consider entries as a proxy for connection if they are recent,
    // but basal specifically might be stale even if entries are flowing (different pathways for some connectors)
    // For now, let's stick to device status and treatments which are more indicative of pump control loop status

    return Math.max(lastDeviceStatusTime, lastTempBasalTime);
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
  const units = $derived(glucoseUnits.current);
  const isMMOL = $derived(units === "mmol");
  const lowThreshold = $derived(
    convertToDisplayUnits(chartConfig.low.threshold ?? 55, units)
  );
  const highThreshold = $derived(
    convertToDisplayUnits(chartConfig.high.threshold ?? 180, units)
  );

  // Y domain for glucose (dynamic based on data, unit-aware)
  const glucoseYMin = $derived(isMMOL ? 2.2 : 40);
  const glucoseYMax = $derived.by(() => {
    const maxSgv = Math.max(...filteredEntries.map((e) => e.sgv ?? 0));
    const maxDisplayValue = convertToDisplayUnits(
      Math.min(400, Math.max(280, maxSgv) + 20),
      units
    );
    return maxDisplayValue;
  });

  // Glucose data for chart (convert to display units)
  const glucoseData = $derived(
    filteredEntries
      .filter((e) => e.sgv !== null && e.sgv !== undefined)
      .map((e) => ({
        time: new Date(e.mills ?? 0),
        sgv: convertToDisplayUnits(e.sgv ?? 0, units),
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
        sgv: convertToDisplayUnits(p.value, units),
      })) ?? []
  );

  const iobPredictionData = $derived(
    predictionData?.curves.iobOnly
      .filter((p) => p.timestamp <= predictionEndTime)
      .map((p) => ({
        time: new Date(p.timestamp),
        sgv: convertToDisplayUnits(p.value, units),
      })) ?? []
  );

  const uamPredictionData = $derived(
    predictionData?.curves.uam
      .filter((p) => p.timestamp <= predictionEndTime)
      .map((p) => ({
        time: new Date(p.timestamp),
        sgv: convertToDisplayUnits(p.value, units),
      })) ?? []
  );

  const cobPredictionData = $derived(
    predictionData?.curves.cob
      .filter((p) => p.timestamp <= predictionEndTime)
      .map((p) => ({
        time: new Date(p.timestamp),
        sgv: convertToDisplayUnits(p.value, units),
      })) ?? []
  );

  const zeroTempPredictionData = $derived(
    predictionData?.curves.zeroTemp
      .filter((p) => p.timestamp <= predictionEndTime)
      .map((p) => ({
        time: new Date(p.timestamp),
        sgv: convertToDisplayUnits(p.value, units),
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
          min: convertToDisplayUnits(Math.min(...valuesAtTime), units),
          max: convertToDisplayUnits(Math.max(...valuesAtTime), units),
          mid: convertToDisplayUnits(
            (Math.min(...valuesAtTime) + Math.max(...valuesAtTime)) / 2,
            units
          ),
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
    <!-- Compound chart using grid-stack -->
    <div class="h-[450px] grid stack p-4">
      <!-- ===== BASAL CHART (TOP) ===== -->
      <Chart
        data={basalData}
        x={(d) => d.time}
        y={(d) => d.rate}
        debug
        xScale={scaleTime()}
        xDomain={[chartXDomain.from, chartXDomain.to]}
        yDomain={[maxBasalRate, 0]}
        yRange={({ height }) => [height * trackRatios.basal, 0]}
        padding={{ left: 48, bottom: 0, top: 8, right: 48 }}
      >
        <Svg>
          {#if staleBasalData}
            <ChartClipPath>
              <AnnotationRange
                x={[
                  staleBasalData.start.getTime(),
                  staleBasalData.end.getTime(),
                ]}
                y={[maxBasalRate, 0]}
                pattern={{
                  size: 8,

                  lines: {
                    rotate: -45,
                    opacity: 0.1,
                  },
                }}
              />
              <!-- Optional: Add a line at the start of stale period -->
              <Rule
                x={staleBasalData.start}
                class="stroke-yellow-500/50 stroke-1"
                stroke-dasharray="2,2"
              />
            </ChartClipPath>
          {/if}
          <!-- Scheduled basal rate line (profile rate without temp modifications) -->
          {#if scheduledBasalData.length > 0}
            <Spline
              data={scheduledBasalData}
              x={(d) => d.time}
              y={(d) => d.rate}
              curve={curveStepAfter}
              class="stroke-muted-foreground/50 stroke-1 fill-none"
              stroke-dasharray="4,4"
            />
          {/if}

          <!-- Basal axis on right -->
          <Axis
            placement="right"
            ticks={2}
            tickLabelProps={{
              class: "text-[9px] fill-muted-foreground",
            }}
          />

          <!-- Track label -->
          <Text
            x={4}
            y={4}
            class="text-[8px] fill-muted-foreground font-medium"
          >
            BASAL
          </Text>

          <!-- Effective basal area (includes temp basals - drips from top due to inverted yRange) -->
          {#if basalData.length > 0}
            <!-- This has to use y0 and y1, don't change it -->
            <Area
              y0={(d) => d.rate}
              y1={(d) => 0}
              curve={curveStepAfter}
              fill="var(--insulin-basal)"
              class="stroke-[var(--insulin)] stroke-1"
            />
          {/if}
        </Svg>
      </Chart>

      <!-- ===== IOB CHART (BOTTOM) with Treatment Markers ===== -->
      <Chart
        data={iobData}
        x={(d) => d.time}
        y="value"
        xScale={scaleTime()}
        xDomain={[chartXDomain.from, chartXDomain.to]}
        yDomain={[maxIOB, 0]}
        yRange={({ height }) => [height * (1 - trackRatios.iob), height]}
        padding={{ left: 48, bottom: 0, top: 0, right: 48 }}
      >
        <Svg>
          <!-- IOB axis on right -->
          <Axis
            placement="right"
            ticks={2}
            tickLabelProps={{ class: "text-[9px] fill-muted-foreground" }}
          />

          <!-- Track label -->
          <Text
            x={4}
            y={4}
            class="text-[8px] fill-muted-foreground font-medium"
          >
            IOB
          </Text>

          <!-- IOB area -->
          {#if iobData.length > 0 && iobData.some((d) => d.value > 0.01)}
            <Area
              y0={0}
              y1="value"
              motion="spring"
              curve={curveMonotoneX}
              fill="var(--iob-basal)"
              class="stroke-[var(--insulin)] stroke-1"
            />
          {/if}
          <!-- Bolus markers with values (triangles pointing up) -->
          {#each bolusMarkersForIob as marker}
            <Group x={marker.time.getTime()} y={0}>
              <Polygon
                points={[
                  { x: 0, y: -10 },
                  { x: -5, y: 0 },
                  { x: 5, y: 0 },
                ]}
                fill="var(--insulin-bolus)"
                class="opacity-90"
              />
              <Text
                y={-14}
                textAnchor="middle"
                class="text-[8px] fill-[var(--insulin-bolus)] font-medium"
              >
                {marker.insulin.toFixed(1)}U
              </Text>
            </Group>
          {/each}

          <!-- Carb markers with values (triangles pointing down) -->
          {#each carbMarkersForIob as marker}
            <Group x={marker.time.getTime()} y={0}>
              <Polygon
                points={[
                  { x: 0, y: 10 },
                  { x: -5, y: 0 },
                  { x: 5, y: 0 },
                ]}
                fill="var(--carbs)"
                class="opacity-90"
              />
              <Text
                y={18}
                textAnchor="middle"
                class="text-[8px] fill-[var(--carbs)] font-medium"
              >
                {marker.carbs}g
              </Text>
            </Group>
          {/each}

          <Highlight points lines />
        </Svg>
      </Chart>

      <!-- ===== GLUCOSE CHART (MIDDLE) - Main glucose display with highlights ===== -->
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
            <!-- High threshold line -->
            <Rule
              y={highThreshold}
              class="stroke-[var(--glucose-high)]/50"
              stroke-dasharray="4,4"
            />

            <!-- Low threshold line -->
            <Rule
              y={lowThreshold}
              class="stroke-[var(--glucose-very-low)]/50"
              stroke-dasharray="4,4"
            />

            <!-- Glucose axis on left -->
            <Axis
              placement="left"
              ticks={5}
              tickLabelProps={{ class: "text-xs fill-muted-foreground" }}
            />

            <!-- Glucose line -->
            <Spline
              class="stroke-[var(--glucose-in-range)] stroke-2 fill-none"
              motion="spring"
              curve={curveMonotoneX}
            />

            <!-- Glucose points -->
            {#each glucoseData as point}
              <Points
                data={[point]}
                r={3}
                fill={point.color}
                class="opacity-90"
              />
            {/each}

            <!-- Prediction visualizations with boundary for graceful loading -->
            <svelte:boundary>
              {#snippet pending()}
                <!-- Prediction loading indicator - subtle dashed line -->
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
                  y="sgv"
                  curve={curveMonotoneX}
                  class="stroke-slate-500/50 stroke-1 fill-none animate-pulse"
                  stroke-dasharray="4,4"
                />
                <Text
                  x={chartXDomain.to.getTime() + 5 * 60 * 1000}
                  y={glucoseData.at(-1)?.sgv ?? 100}
                  class="text-[9px] fill-slate-500 animate-pulse"
                >
                  Loading predictions...
                </Text>
              {/snippet}

              {#snippet failed(error)}
                <Text x={50} y={50} class="text-xs fill-red-400">
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
                    y0="max"
                    y1="min"
                    curve={curveMonotoneX}
                    class="fill-purple-500/20 stroke-none"
                    motion="spring"
                  />
                  <Spline
                    data={predictionConeData}
                    x={(d) => d.time}
                    y="mid"
                    curve={curveMonotoneX}
                    motion="spring"
                    class="stroke-purple-400 stroke-1 fill-none"
                    stroke-dasharray="4,2"
                  />
                {:else if predictionMode === "lines"}
                  {#if predictionCurveData.length > 0}
                    <Spline
                      data={predictionCurveData}
                      y="sgv"
                      curve={curveMonotoneX}
                      motion="spring"
                      class="stroke-purple-400 stroke-2 fill-none"
                      stroke-dasharray="6,3"
                    />
                  {/if}
                  {#if iobPredictionData.length > 0}
                    <Spline
                      data={iobPredictionData}
                      y="sgv"
                      curve={curveMonotoneX}
                      motion="spring"
                      class="stroke-cyan-400 stroke-1 fill-none opacity-80"
                      stroke-dasharray="4,2"
                    />
                  {/if}
                  {#if zeroTempPredictionData.length > 0}
                    <Spline
                      data={zeroTempPredictionData}
                      y="sgv"
                      curve={curveMonotoneX}
                      motion="spring"
                      class="stroke-orange-400 stroke-1 fill-none opacity-80"
                      stroke-dasharray="4,2"
                    />
                  {/if}
                  {#if uamPredictionData.length > 0}
                    <Spline
                      data={uamPredictionData}
                      y="sgv"
                      curve={curveMonotoneX}
                      motion="spring"
                      class="stroke-green-400 stroke-1 fill-none opacity-80"
                      stroke-dasharray="4,2"
                    />
                  {/if}
                  {#if cobPredictionData.length > 0}
                    <Spline
                      data={cobPredictionData}
                      y="sgv"
                      motion="spring"
                      curve={curveMonotoneX}
                      class="stroke-yellow-400 stroke-1 fill-none opacity-80"
                      stroke-dasharray="4,2"
                    />
                  {/if}
                {:else if predictionMode === "main" && predictionCurveData.length > 0}
                  <Spline
                    data={predictionCurveData}
                    y="sgv"
                    motion="spring"
                    curve={curveMonotoneX}
                    class="stroke-purple-400 stroke-2 fill-none"
                    stroke-dasharray="6,3"
                  />
                {:else if predictionMode === "iob" && iobPredictionData.length > 0}
                  <Spline
                    data={iobPredictionData}
                    y="sgv"
                    motion="spring"
                    curve={curveMonotoneX}
                    class="stroke-cyan-400 stroke-2 fill-none"
                    stroke-dasharray="6,3"
                  />
                {:else if predictionMode === "zt" && zeroTempPredictionData.length > 0}
                  <Spline
                    data={zeroTempPredictionData}
                    y="sgv"
                    motion="spring"
                    curve={curveMonotoneX}
                    class="stroke-orange-400 stroke-2 fill-none"
                    stroke-dasharray="6,3"
                  />
                {:else if predictionMode === "uam" && uamPredictionData.length > 0}
                  <Spline
                    data={uamPredictionData}
                    y="sgv"
                    motion="spring"
                    curve={curveMonotoneX}
                    class="stroke-green-400 stroke-2 fill-none"
                    stroke-dasharray="6,3"
                  />
                {:else if predictionMode === "cob" && cobPredictionData.length > 0}
                  <Spline
                    data={cobPredictionData}
                    y="sgv"
                    motion="spring"
                    curve={curveMonotoneX}
                    class="stroke-yellow-400 stroke-2 fill-none"
                    stroke-dasharray="6,3"
                  />
                {/if}
              {/if}
              {#if showPredictions && predictionError}
                <Text x={50} y={50} class="text-xs fill-red-400">
                  Prediction unavailable
                </Text>
              {/if}
            </svelte:boundary>

            <!-- X-Axis (bottom) -->
            <Axis
              placement="bottom"
              format={(v) => (v instanceof Date ? formatTime(v) : String(v))}
              tickLabelProps={{ class: "text-xs fill-muted-foreground" }}
            />

            <!-- Glucose highlight (main) -->
            <Highlight points lines />

            <!-- Basal highlight with remapped scale -->
            <Highlight
              data={basalData}
              points={{ class: "fill-[var(--insulin-basal)]" }}
              y={(d) => {
                // Remap basal to top track area (inverted: 0 at top)
                const basalAreaHeight = context.height * trackRatios.basal;
                const normalized = d.rate / maxBasalRate;
                return basalAreaHeight * normalized;
              }}
            />

            <!-- IOB highlight with remapped scale -->
            <Highlight
              data={iobData}
              points={{ class: "fill-[var(--iob-basal)]" }}
              y={(d) => {
                // Remap IOB to bottom track area
                const iobAreaTop = context.height * (1 - trackRatios.iob);
                const iobAreaHeight = context.height * trackRatios.iob;
                const normalized = d.value / maxIOB;
                return iobAreaTop + iobAreaHeight * (1 - normalized);
              }}
            />
          </Svg>

          <Tooltip.Root
            {context}
            class="bg-slate-900/95 border border-slate-800 rounded-lg shadow-xl text-xs z-50 backdrop-blur-sm"
          >
            {#snippet children({ data })}
              {@const activeBasal = findBasalValue(basalData, data.time)}
              {@const activeIob = findSeriesValue(iobData, data.time)}

              <Tooltip.Header
                value={data?.time?.toLocaleTimeString([], {
                  hour: "numeric",
                  minute: "2-digit",
                })}
                format="time"
                class="text-slate-300 border-b border-slate-800 pb-1 mb-1 font-mono"
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
