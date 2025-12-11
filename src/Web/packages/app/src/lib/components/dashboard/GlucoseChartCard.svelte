<script lang="ts">
  import type { Entry, Treatment } from "$lib/api";
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
    ChartClipPath,
    Tooltip,
    Layer,
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
    glucoseUnits,
    predictionMinutes,
    predictionEnabled,
  } from "$lib/stores/appearance-store.svelte";
  import { convertToDisplayUnits } from "$lib/utils/formatting";
  import {
    Select,
    SelectContent,
    SelectItem,
    SelectTrigger,
  } from "$lib/components/ui/select";

  interface ComponentProps {
    entries?: Entry[];
    treatments?: Treatment[];
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
  let predictionMode = $state<PredictionDisplayMode>("cone");

  // Sync prediction mode with algorithm settings model
  $effect(() => {
    const modelToMode: Record<string, PredictionDisplayMode> = {
      ar2: "cone",
      linear: "cone",
      iob: "iob",
      cob: "cob",
      uam: "uam",
      cone: "cone",
      lines: "lines",
    };
    predictionMode = modelToMode[predictionModel] ?? "cone";
  });

  // Suppress unused variable warnings
  void isf;
  void carbRatio;

  // Fetch predictions when enabled
  $effect(() => {
    if (showPredictions) {
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
          new Date().getTime() - parseInt(selectedTimeRange) * 60 * 60 * 1000
        ),
    to: dateRange ? normalizeDate(dateRange.to, new Date()) : new Date(),
  });

  // Fetch server-side chart data when date range changes
  $effect(() => {
    const startTime = displayDateRange.from.getTime();
    const endTime = displayDateRange.to.getTime();

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

  // Prediction curve data
  const predictionCurveData = $derived(
    predictionData?.curves.main.map((p) => ({
      time: new Date(p.timestamp),
      sgv: convertToDisplayUnits(p.value, units),
    })) ?? []
  );

  const iobPredictionData = $derived(
    predictionData?.curves.iobOnly.map((p) => ({
      time: new Date(p.timestamp),
      sgv: convertToDisplayUnits(p.value, units),
    })) ?? []
  );

  const uamPredictionData = $derived(
    predictionData?.curves.uam.map((p) => ({
      time: new Date(p.timestamp),
      sgv: convertToDisplayUnits(p.value, units),
    })) ?? []
  );

  const cobPredictionData = $derived(
    predictionData?.curves.cob.map((p) => ({
      time: new Date(p.timestamp),
      sgv: convertToDisplayUnits(p.value, units),
    })) ?? []
  );

  const zeroTempPredictionData = $derived(
    predictionData?.curves.zeroTemp.map((p) => ({
      time: new Date(p.timestamp),
      sgv: convertToDisplayUnits(p.value, units),
    })) ?? []
  );

  // Prediction cone data
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
    return primaryCurve.map((point, i) => {
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
  const effectiveDefaultBasalRate = $derived(
    serverChartData?.defaultBasalRate ?? defaultBasalRate
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

  // Bolus marker data
  const bolusMarkerData = $derived(
    bolusTreatments.map((t) => ({
      time: new Date(getTreatmentTime(t)),
      value: glucoseYMax - 10,
      insulin: t.insulin ?? 0,
    }))
  );

  // Carb marker data
  const carbMarkerData = $derived(
    carbTreatments.map((t) => ({
      time: new Date(getTreatmentTime(t)),
      value: glucoseYMin + 10,
      carbs: t.carbs ?? 0,
    }))
  );

  // ===== COMPOUND CHART CONFIGURATION =====
  const CHART_HEIGHT = 420;
  const GAP = 12; // Gap between tracks
  const GLUCOSE_HEIGHT = 270;
  const BASAL_HEIGHT = 60;
  const IOB_HEIGHT = CHART_HEIGHT - GLUCOSE_HEIGHT - BASAL_HEIGHT - GAP * 2;

  const glucoseYRange = $derived([0, GLUCOSE_HEIGHT]);
  const basalYRange = $derived([0, BASAL_HEIGHT]); // 0 at top (pixel 0), grows down
  const iobYRange = $derived([IOB_HEIGHT, 0]); // 0 at bottom, grows up

  // Tracks vertical offsets (y=0 is at TOP in SVG)
  const basalYOffset = -CHART_HEIGHT + 100; // Top
  const glucoseYOffset = -72; // Middle (72px from top)
  const iobYOffset = 0; // Bottom (354px from top)

  // Scales
  const glucoseScale = $derived(
    scaleLinear().domain([glucoseYMin, glucoseYMax]).range(glucoseYRange)
  );

  const basalScale = $derived(
    scaleLinear().domain([0, maxBasalRate]).range(basalYRange)
  );

  const iobScale = $derived(scaleLinear().domain([0, maxIOB]).range(iobYRange));

  // Identity scale for root chart (maps pixels to pixels)
  const identityScale = scaleLinear()
    .domain([0, CHART_HEIGHT])
    .range([0, CHART_HEIGHT]);
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
  function findBasalValue(series: { time: Date; rate: number }[], time: Date) {
    if (!series || series.length === 0) return undefined;
    const i = bisectDate(series, time, 1);
    return series[i - 1];
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
        <!-- Prediction mode selector -->
        <!-- Only show mode selector if predictions are enabled in settings/store AND prop overrides -->
        {#if showPredictions && predictionEnabled.current}
          <ToggleGroup.Root
            type="single"
            bind:value={predictionMode}
            class="bg-slate-900 rounded-lg p-0.5"
          >
            <ToggleGroup.Item
              value="cone"
              class="px-2 py-1 text-xs font-medium text-slate-400 data-[state=on]:bg-purple-700 data-[state=on]:text-slate-100 rounded-md transition-colors"
              title="Cone of probabilities"
            >
              Cone
            </ToggleGroup.Item>
            <ToggleGroup.Item
              value="lines"
              class="px-2 py-1 text-xs font-medium text-slate-400 data-[state=on]:bg-purple-700 data-[state=on]:text-slate-100 rounded-md transition-colors"
              title="All prediction lines"
            >
              Lines
            </ToggleGroup.Item>
            <ToggleGroup.Item
              value="iob"
              class="px-2 py-1 text-xs font-medium text-slate-400 data-[state=on]:bg-cyan-700 data-[state=on]:text-slate-100 rounded-md transition-colors"
              title="IOB only"
            >
              IOB
            </ToggleGroup.Item>
            <ToggleGroup.Item
              value="zt"
              class="px-2 py-1 text-xs font-medium text-slate-400 data-[state=on]:bg-orange-700 data-[state=on]:text-slate-100 rounded-md transition-colors"
              title="Zero Temp"
            >
              ZT
            </ToggleGroup.Item>
            <ToggleGroup.Item
              value="uam"
              class="px-2 py-1 text-xs font-medium text-slate-400 data-[state=on]:bg-green-700 data-[state=on]:text-slate-100 rounded-md transition-colors"
              title="UAM"
            >
              UAM
            </ToggleGroup.Item>
          </ToggleGroup.Root>
        {/if}

        <!-- Prediction time/enable selector -->
        {#if showPredictions}
          <div class="bg-slate-900 rounded-lg p-0.5">
            <Select
              type="single"
              value={predictionEnabled.current
                ? predictionMinutes.current.toString()
                : "disabled"}
              onValueChange={(v) => {
                if (v === "disabled") {
                  predictionEnabled.current = false;
                } else {
                  predictionEnabled.current = true;
                  predictionMinutes.current = parseInt(v);
                }
              }}
            >
              <SelectTrigger
                class="h-7 w-[90px] bg-transparent border-none text-xs text-slate-400 focus:ring-0 focus:ring-offset-0 px-2 data-[placeholder]:text-slate-400"
              >
                <div class="flex items-center gap-1.5 truncate">
                  {#if !predictionEnabled.current}
                    <span class="text-slate-500">Off</span>
                  {:else}
                    <span>
                      {predictionMinutes.current < 60
                        ? `${predictionMinutes.current}m`
                        : `${predictionMinutes.current / 60}h`}
                    </span>
                  {/if}
                </div>
              </SelectTrigger>
              <SelectContent>
                <SelectItem
                  value="disabled"
                  class="text-xs text-muted-foreground"
                >
                  Disable
                </SelectItem>
                <SelectItem value="15" class="text-xs">15 min</SelectItem>
                <SelectItem value="30" class="text-xs">30 min</SelectItem>
                <SelectItem value="60" class="text-xs">1 hour</SelectItem>
                <SelectItem value="120" class="text-xs">2 hours</SelectItem>
                <SelectItem value="180" class="text-xs">3 hours</SelectItem>
                <SelectItem value="240" class="text-xs">4 hours</SelectItem>
              </SelectContent>
            </Select>
          </div>
        {/if}

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
    <div class="h-[420px] relative p-4">
      <Chart
        data={glucoseData}
        x={(d) => d.time}
        y={(d) => glucoseScale(d.sgv)}
        xScale={scaleTime()}
        yScale={identityScale}
        xDomain={[chartXDomain.from, chartXDomain.to]}
        yDomain={[0, CHART_HEIGHT]}
        padding={{ left: 12, bottom: 30, top: 8, right: 12 }}
        tooltip={{ mode: "quadtree-x" }}
      >
        <Svg>
          <!-- <ChartClipPath> -->
          <Layer type="svg">
            <!-- ===== BASAL TRACK (TOP) ===== -->

            <Group y={basalYOffset}>
              <Rule
                y={basalScale(effectiveDefaultBasalRate)}
                class="stroke-muted-foreground"
                stroke-dasharray="4,4"
              />
              <Axis
                placement="right"
                scale={basalScale}
                ticks={2}
                tickLabelProps={{
                  class: "text-[9px] fill-muted-foreground basal-axis",
                }}
              />

              <Text
                x={4}
                y={0}
                class="text-[8px] fill-muted-foreground font-medium"
              >
                BASAL
              </Text>
              <Text
                x={44}
                y={basalScale(effectiveDefaultBasalRate)}
                textAnchor="end"
                dy={-2}
                class="text-[7px] fill-muted-foreground"
              >
                {effectiveDefaultBasalRate.toFixed(1)}U/hr
              </Text>

              {#if basalData.length > 0}
                <Area
                  data={basalData}
                  x={(d) => d.time}
                  y0={basalScale(0)}
                  y1={(d) => basalScale(d.rate)}
                  curve={curveStepAfter}
                  fill="var(--insulin-basal)"
                  class="stroke-[var(--insulin)] stroke-1 basal-area"
                />
              {/if}
            </Group>

            <!-- ===== GLUCOSE TRACK (MIDDLE) ===== -->
            <Group y={glucoseYOffset}>
              <!-- High threshold line -->
              <Rule
                y={glucoseScale(highThreshold)}
                class="stroke-[var(--glucose-high)]/50"
                stroke-dasharray="4,4"
              />

              <!-- Low threshold line -->
              <Rule
                y={glucoseScale(lowThreshold)}
                class="stroke-[var(--glucose-very-low)]/50"
                stroke-dasharray="4,4"
              />

              <Axis
                placement="left"
                scale={glucoseScale}
                tickLabelProps={{ class: "text-xs fill-muted-foreground" }}
              />

              <!-- Glucose Y-Axis labels (left) -->
              <!-- Using Axis component instead of manual Text for better cleanup -->
              <Text
                x={4}
                y={glucoseScale(highThreshold)}
                textAnchor="start"
                dy={-4}
                class="text-[10px] fill-[var(--glucose-high)]"
              >
                {highThreshold}
              </Text>
              <Text
                x={4}
                y={glucoseScale(lowThreshold)}
                textAnchor="start"
                dy={12}
                class="text-[10px] fill-[var(--glucose-very-low)]"
              >
                {lowThreshold}
              </Text>

              <Highlight points lines />

              <!-- Glucose line -->
              <Spline
                class="stroke-[var(--insulin)] stroke-2 fill-none"
                y={(d) => glucoseScale(d.sgv)}
              />

              <!-- Glucose points -->
              {#each glucoseData as point}
                <Points
                  data={[point]}
                  r={3}
                  y={(d) => glucoseScale(d.sgv)}
                  fill={point.color}
                  class="opacity-90"
                />
              {/each}

              <!-- Prediction visualizations -->
              {#if showPredictions && predictionEnabled.current && predictionData}
                {#if predictionMode === "cone" && predictionConeData.length > 0}
                  <Area
                    data={predictionConeData}
                    x={(d) => d.time}
                    y0={(d) => glucoseScale(d.max)}
                    y1={(d) => glucoseScale(d.min)}
                    curve={curveMonotoneX}
                    class="fill-purple-500/20 stroke-none"
                  />
                  <Spline
                    data={predictionConeData}
                    x={(d) => d.time}
                    y={(d) => glucoseScale(d.mid)}
                    curve={curveMonotoneX}
                    class="stroke-purple-400 stroke-1 fill-none"
                    stroke-dasharray="4,2"
                  />
                {:else if predictionMode === "lines"}
                  {#if predictionCurveData.length > 0}
                    <Spline
                      data={predictionCurveData}
                      y={(d) => glucoseScale(d.sgv)}
                      curve={curveMonotoneX}
                      class="stroke-purple-400 stroke-2 fill-none"
                      stroke-dasharray="6,3"
                    />
                  {/if}
                  {#if iobPredictionData.length > 0}
                    <Spline
                      data={iobPredictionData}
                      y={(d) => glucoseScale(d.sgv)}
                      curve={curveMonotoneX}
                      class="stroke-cyan-400 stroke-1 fill-none opacity-80"
                      stroke-dasharray="4,2"
                    />
                  {/if}
                  {#if zeroTempPredictionData.length > 0}
                    <Spline
                      data={zeroTempPredictionData}
                      y={(d) => glucoseScale(d.sgv)}
                      curve={curveMonotoneX}
                      class="stroke-orange-400 stroke-1 fill-none opacity-80"
                      stroke-dasharray="4,2"
                    />
                  {/if}
                  {#if uamPredictionData.length > 0}
                    <Spline
                      data={uamPredictionData}
                      y={(d) => glucoseScale(d.sgv)}
                      curve={curveMonotoneX}
                      class="stroke-green-400 stroke-1 fill-none opacity-80"
                      stroke-dasharray="4,2"
                    />
                  {/if}
                  {#if cobPredictionData.length > 0}
                    <Spline
                      data={cobPredictionData}
                      y={(d) => glucoseScale(d.sgv)}
                      curve={curveMonotoneX}
                      class="stroke-yellow-400 stroke-1 fill-none opacity-80"
                      stroke-dasharray="4,2"
                    />
                  {/if}
                {:else if predictionMode === "main" && predictionCurveData.length > 0}
                  <Spline
                    data={predictionCurveData}
                    y={(d) => glucoseScale(d.sgv)}
                    curve={curveMonotoneX}
                    class="stroke-purple-400 stroke-2 fill-none"
                    stroke-dasharray="6,3"
                  />
                {:else if predictionMode === "iob" && iobPredictionData.length > 0}
                  <Spline
                    data={iobPredictionData}
                    y={(d) => glucoseScale(d.sgv)}
                    curve={curveMonotoneX}
                    class="stroke-cyan-400 stroke-2 fill-none"
                    stroke-dasharray="6,3"
                  />
                {:else if predictionMode === "zt" && zeroTempPredictionData.length > 0}
                  <Spline
                    data={zeroTempPredictionData}
                    y={(d) => glucoseScale(d.sgv)}
                    curve={curveMonotoneX}
                    class="stroke-orange-400 stroke-2 fill-none"
                    stroke-dasharray="6,3"
                  />
                {:else if predictionMode === "uam" && uamPredictionData.length > 0}
                  <Spline
                    data={uamPredictionData}
                    y={(d) => glucoseScale(d.sgv)}
                    curve={curveMonotoneX}
                    class="stroke-green-400 stroke-2 fill-none"
                    stroke-dasharray="6,3"
                  />
                {:else if predictionMode === "cob" && cobPredictionData.length > 0}
                  <Spline
                    data={cobPredictionData}
                    y={(d) => glucoseScale(d.sgv)}
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

              <!-- Bolus markers -->
              {#each bolusMarkerData as marker}
                <Group
                  x={marker.time.getTime()}
                  y={glucoseScale(glucoseYMax - 20)}
                >
                  <Polygon
                    points={[
                      { x: 0, y: -8 },
                      { x: -4, y: 0 },
                      { x: 4, y: 0 },
                    ]}
                    fill="var(--insulin-bolus)"
                    class="opacity-90"
                  />
                </Group>
              {/each}

              <!-- Carb markers -->
              {#each carbMarkerData as marker}
                <Group
                  x={marker.time.getTime()}
                  y={glucoseScale(glucoseYMin + 20)}
                >
                  <Polygon
                    points={[
                      { x: 0, y: 8 },
                      { x: -4, y: 0 },
                      { x: 4, y: 0 },
                    ]}
                    fill="var(--carbs)"
                    class="opacity-90"
                  />
                </Group>
              {/each}
            </Group>

            <!-- ===== IOB TRACK (BOTTOM) ===== -->
            <Group y={iobYOffset}>
              <Axis
                placement="right"
                scale={iobScale}
                ticks={2}
                tickLabelProps={{ class: "text-[9px] fill-muted-foreground" }}
              />
              <Text
                x={4}
                y={0}
                class="text-[8px] fill-muted-foreground font-medium"
              >
                IOB
              </Text>
              {#if maxIOB >= 1}
                <Text
                  x={44}
                  y={iobScale(1)}
                  textAnchor="end"
                  class="text-[6px] fill-[var(--insulin)]"
                >
                  1U
                </Text>
              {/if}

              {#if iobData.length > 0 && iobData.some((d) => d.value > 0.01)}
                <Area
                  data={iobData}
                  x={(d) => d.time}
                  y0={iobScale(0)}
                  y1={(d) => iobScale(d.value)}
                  curve={curveMonotoneX}
                  fill="var(--iob-basal)"
                  class="stroke-[var(--insulin)] stroke-1"
                />
              {/if}
            </Group>
          </Layer>
          <!-- </ChartClipPath> -->

          <!-- X-Axis (bottom) -->
          <Axis
            placement="bottom"
            format={(v) => (v instanceof Date ? formatTime(v) : String(v))}
            tickLabelProps={{ class: "text-xs fill-muted-foreground" }}
          />
        </Svg>

        <Tooltip.Root
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
                  label="Basal"
                  value={activeBasal.rate}
                  format={"decimal"}
                  color="var(--insulin-basal)"
                />
              {/if}
            </Tooltip.List>
          {/snippet}
        </Tooltip.Root>
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
