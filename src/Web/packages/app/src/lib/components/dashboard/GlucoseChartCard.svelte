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
    Text,
    ChartClipPath,
  } from "layerchart";
  import { chartConfig } from "$lib/constants";
  import { curveStepAfter, curveMonotoneX } from "d3";
  import { scaleTime, scaleLinear } from "d3-scale";
  import {
    getPredictions,
    type PredictionData,
  } from "$lib/data/predictions.remote";
  import { glucoseUnitsState } from "$lib/stores/appearance-store.svelte";
  import { convertToDisplayUnits } from "$lib/utils/formatting";

  interface ComponentProps {
    entries?: Entry[];
    treatments?: Treatment[];
    demoMode?: boolean;
    dateRange?: {
      from: Date | string;
      to: Date | string;
    };
    /** Default basal rate from profile (U/hr) */
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
    showPredictions = true, // Enable predictions by default
    defaultFocusHours,
    predictionModel = "cone",
  }: ComponentProps = $props();

  // Prediction data state
  let predictionData = $state<PredictionData | null>(null);
  let predictionError = $state<string | null>(null);

  // Prediction display mode: 'cone' shows shaded probability area, 'lines' shows individual curves
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
    // Map algorithm model names to display modes
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

  // Kept for future use - suppress unused variable warnings
  void isf;

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

  // Helper to convert numeric focus hours to valid TimeRangeOption
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

  // Helper to normalize date
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

  // Prediction buffer: extend chart 4 hours into the future when predictions are enabled
  const predictionHours = 4;
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

  // Temp basal treatments
  const tempBasalTreatments = $derived(
    filteredTreatments.filter(
      (t) =>
        t.eventType === "Temp Basal" &&
        (t.rate !== undefined || t.percent !== undefined)
    )
  );

  function getTreatmentTime(t: Treatment): number {
    return t.mills ?? (t.created_at ? new Date(t.created_at).getTime() : 0);
  }

  // Thresholds (convert to display units)
  const units = $derived(glucoseUnitsState.units);
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

  // Prediction curve data for chart (convert to display units)
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

  // Prediction cone data - shows min/max range of all prediction curves (convert to display units)
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

    // Assume all curves have same timestamps, use first curve's timestamps
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

  // Basal data
  const basalData = $derived.by(() => {
    const data: { time: Date; rate: number }[] = [];

    data.push({
      time: displayDateRange.from,
      rate: defaultBasalRate,
    });

    if (tempBasalTreatments.length > 0) {
      const sorted = [...tempBasalTreatments].sort(
        (a, b) => getTreatmentTime(a) - getTreatmentTime(b)
      );

      sorted.forEach((t) => {
        const startTime = getTreatmentTime(t);
        const duration = (t.duration ?? 30) * 60 * 1000;
        const endTime = startTime + duration;
        const rate = t.rate ?? defaultBasalRate;

        data.push({
          time: new Date(startTime - 1),
          rate: defaultBasalRate,
        });
        data.push({ time: new Date(startTime), rate });
        data.push({ time: new Date(endTime), rate });
        data.push({
          time: new Date(endTime + 1),
          rate: defaultBasalRate,
        });
      });
    }

    data.push({
      time: displayDateRange.to,
      rate: defaultBasalRate,
    });

    return data.sort((a, b) => a.time.getTime() - b.time.getTime());
  });

  const maxBasalRate = $derived(
    Math.max(
      defaultBasalRate * 2.5,
      ...tempBasalTreatments.map((t) => t.rate ?? 0)
    )
  );

  // IOB calculation
  const insulinDuration = 4 * 60 * 60 * 1000;

  const iobData = $derived.by(() => {
    const data: { time: Date; iob: number }[] = [];
    const step = 5 * 60 * 1000;
    const xMin = displayDateRange.from.getTime();
    const xMax = displayDateRange.to.getTime();

    for (let t = xMin; t <= xMax; t += step) {
      let iob = 0;
      bolusTreatments.forEach((bolus) => {
        const bolusTime = getTreatmentTime(bolus);
        const elapsed = t - bolusTime;
        if (elapsed >= 0 && elapsed < insulinDuration) {
          const remaining = Math.exp(-elapsed / (insulinDuration / 3));
          iob += (bolus.insulin ?? 0) * remaining;
        }
      });
      data.push({ time: new Date(t), iob });
    }
    return data;
  });

  const maxIOB = $derived(Math.max(3, ...iobData.map((d) => d.iob)));

  // COB calculation
  const carbDuration = 3 * 60 * 60 * 1000;

  const cobData = $derived.by(() => {
    const data: { time: Date; cob: number }[] = [];
    const step = 5 * 60 * 1000;
    const xMin = displayDateRange.from.getTime();
    const xMax = displayDateRange.to.getTime();

    for (let t = xMin; t <= xMax; t += step) {
      let cob = 0;
      carbTreatments.forEach((carb) => {
        const carbTime = getTreatmentTime(carb);
        const elapsed = t - carbTime;
        if (elapsed >= 0 && elapsed < carbDuration) {
          const remaining = 1 - elapsed / carbDuration;
          cob += (carb.carbs ?? 0) * remaining;
        }
      });
      data.push({ time: new Date(t), cob });
    }
    return data;
  });

  // Kept for future use when COB chart is implemented
  const maxCOB = $derived(
    Math.max(carbRatio * 3, ...cobData.map((d) => d.cob))
  );
  // Suppress unused variable warning while keeping reactivity
  $effect(() => {
    void maxCOB;
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

  // Bolus marker data for Points component
  const bolusMarkerData = $derived(
    bolusTreatments.map((t) => ({
      time: new Date(getTreatmentTime(t)),
      value: glucoseYMax - 10,
      insulin: t.insulin ?? 0,
    }))
  );

  // Carb marker data for Points component
  const carbMarkerData = $derived(
    carbTreatments.map((t) => ({
      time: new Date(getTreatmentTime(t)),
      value: glucoseYMin + 10,
      carbs: t.carbs ?? 0,
    }))
  );

  $inspect(predictionData);
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
        <!-- Prediction mode selector (shown when predictions enabled) -->
        {#if showPredictions}
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
    <div class="h-[420px] relative">
      <!-- Main Glucose Chart -->
      <div class="h-[280px]">
        <Chart
          data={glucoseData}
          x={(d) => d.time}
          y={(d) => d.sgv}
          xScale={scaleTime()}
          yScale={scaleLinear()}
          xDomain={[chartXDomain.from, chartXDomain.to]}
          yDomain={[glucoseYMin, glucoseYMax]}
          padding={{ left: 48, bottom: 30, top: 8, right: 12 }}
        >
          <Svg>
            <ChartClipPath>
              <!-- High threshold line -->
              <Rule
                y={highThreshold}
                class="stroke-amber-500/50"
                stroke-dasharray="4,4"
              />

              <!-- Low threshold line -->
              <Rule
                y={lowThreshold}
                class="stroke-red-500/50"
                stroke-dasharray="4,4"
              />

              <!-- Glucose line -->
              <Spline class="stroke-blue-400 stroke-2 fill-none" />

              <!-- Glucose points with color based on value -->
              {#each glucoseData as point}
                <Points
                  data={[point]}
                  x={(d) => d.time}
                  y={(d) => d.sgv}
                  r={3}
                  fill={point.color}
                  class="opacity-90"
                />
              {/each}

              <!-- Prediction visualizations based on mode -->
              {#if showPredictions && predictionData}
                {#if predictionMode === "cone" && predictionConeData.length > 0}
                  <!-- Cone of probabilities (shaded area between min and max) -->
                  <Area
                    data={predictionConeData}
                    x={(d) => d.time}
                    y0={(d) => d.min}
                    y1={(d) => d.max}
                    curve={curveMonotoneX}
                    class="fill-purple-500/20 stroke-none"
                  />
                  <!-- Center line of cone -->
                  <Spline
                    data={predictionConeData}
                    x={(d) => d.time}
                    y={(d) => d.mid}
                    curve={curveMonotoneX}
                    class="stroke-purple-400 stroke-1 fill-none"
                    stroke-dasharray="4,2"
                  />
                {:else if predictionMode === "lines"}
                  <!-- All prediction lines -->
                  {#if predictionCurveData.length > 0}
                    <Spline
                      data={predictionCurveData}
                      x={(d) => d.time}
                      y={(d) => d.sgv}
                      curve={curveMonotoneX}
                      class="stroke-purple-400 stroke-2 fill-none"
                      stroke-dasharray="6,3"
                    />
                  {/if}
                  {#if iobPredictionData.length > 0}
                    <Spline
                      data={iobPredictionData}
                      x={(d) => d.time}
                      y={(d) => d.sgv}
                      curve={curveMonotoneX}
                      class="stroke-cyan-400 stroke-1 fill-none opacity-80"
                      stroke-dasharray="4,2"
                    />
                  {/if}
                  {#if zeroTempPredictionData.length > 0}
                    <Spline
                      data={zeroTempPredictionData}
                      x={(d) => d.time}
                      y={(d) => d.sgv}
                      curve={curveMonotoneX}
                      class="stroke-orange-400 stroke-1 fill-none opacity-80"
                      stroke-dasharray="4,2"
                    />
                  {/if}
                  {#if uamPredictionData.length > 0}
                    <Spline
                      data={uamPredictionData}
                      x={(d) => d.time}
                      y={(d) => d.sgv}
                      curve={curveMonotoneX}
                      class="stroke-green-400 stroke-1 fill-none opacity-80"
                      stroke-dasharray="4,2"
                    />
                  {/if}
                  {#if cobPredictionData.length > 0}
                    <Spline
                      data={cobPredictionData}
                      x={(d) => d.time}
                      y={(d) => d.sgv}
                      curve={curveMonotoneX}
                      class="stroke-yellow-400 stroke-1 fill-none opacity-80"
                      stroke-dasharray="4,2"
                    />
                  {/if}
                {:else if predictionMode === "main" && predictionCurveData.length > 0}
                  <Spline
                    data={predictionCurveData}
                    x={(d) => d.time}
                    y={(d) => d.sgv}
                    curve={curveMonotoneX}
                    class="stroke-purple-400 stroke-2 fill-none"
                    stroke-dasharray="6,3"
                  />
                {:else if predictionMode === "iob" && iobPredictionData.length > 0}
                  <Spline
                    data={iobPredictionData}
                    x={(d) => d.time}
                    y={(d) => d.sgv}
                    curve={curveMonotoneX}
                    class="stroke-cyan-400 stroke-2 fill-none"
                    stroke-dasharray="6,3"
                  />
                {:else if predictionMode === "zt" && zeroTempPredictionData.length > 0}
                  <Spline
                    data={zeroTempPredictionData}
                    x={(d) => d.time}
                    y={(d) => d.sgv}
                    curve={curveMonotoneX}
                    class="stroke-orange-400 stroke-2 fill-none"
                    stroke-dasharray="6,3"
                  />
                {:else if predictionMode === "uam" && uamPredictionData.length > 0}
                  <Spline
                    data={uamPredictionData}
                    x={(d) => d.time}
                    y={(d) => d.sgv}
                    curve={curveMonotoneX}
                    class="stroke-green-400 stroke-2 fill-none"
                    stroke-dasharray="6,3"
                  />
                {:else if predictionMode === "cob" && cobPredictionData.length > 0}
                  <Spline
                    data={cobPredictionData}
                    x={(d) => d.time}
                    y={(d) => d.sgv}
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
                <Group x={marker.time.getTime()} y={glucoseYMax - 20}>
                  <Polygon
                    points={[
                      { x: 0, y: -8 },
                      { x: -4, y: 0 },
                      { x: 4, y: 0 },
                    ]}
                    fill="rgb(59 130 246)"
                    class="opacity-90"
                  />
                </Group>
              {/each}

              <!-- Carb markers -->
              {#each carbMarkerData as marker}
                <Group x={marker.time.getTime()} y={glucoseYMin + 20}>
                  <Polygon
                    points={[
                      { x: 0, y: 8 },
                      { x: -4, y: 0 },
                      { x: 4, y: 0 },
                    ]}
                    fill="rgb(245 158 11)"
                    class="opacity-90"
                  />
                </Group>
              {/each}
            </ChartClipPath>

            <!-- Axes -->
            <Axis
              placement="left"
              format={(v) => String(v)}
              tickLabelProps={{ class: "text-xs fill-slate-400" }}
            />
            <Axis
              placement="bottom"
              format={(v) => (v instanceof Date ? formatTime(v) : String(v))}
              tickLabelProps={{ class: "text-xs fill-slate-400" }}
            />

            <!-- Threshold labels -->
            <Text
              x={98}
              y={highThreshold}
              textAnchor="end"
              dy={-4}
              class="text-[10px] fill-amber-500"
            >
              {highThreshold}
            </Text>
            <Text
              x={98}
              y={lowThreshold}
              textAnchor="end"
              dy={12}
              class="text-[10px] fill-red-500"
            >
              {lowThreshold}
            </Text>
          </Svg>
        </Chart>
      </div>

      <!-- Basal Chart -->
      <div class="h-[60px] mt-1">
        <Chart
          data={basalData}
          x={(d) => d.time}
          y={(d) => d.rate}
          xScale={scaleTime()}
          yScale={scaleLinear()}
          xDomain={[displayDateRange.from, displayDateRange.to]}
          yDomain={[0, maxBasalRate]}
          padding={{ left: 48, bottom: 0, top: 4, right: 12 }}
        >
          <Svg>
            <ChartClipPath>
              <!-- Default basal reference line -->
              <Rule
                y={defaultBasalRate}
                class="stroke-slate-500"
                stroke-dasharray="4,4"
              />

              <!-- Basal area -->
              <Area
                y0={0}
                curve={curveStepAfter}
                class="fill-blue-500/40 stroke-blue-400 stroke-1"
              />
            </ChartClipPath>

            <!-- Label -->
            <Text x={4} y={4} class="text-[8px] fill-slate-500 font-medium">
              BASAL
            </Text>
            <Text
              x={98}
              y={defaultBasalRate}
              textAnchor="end"
              dy={-2}
              class="text-[7px] fill-slate-400"
            >
              {defaultBasalRate.toFixed(1)}U/hr
            </Text>
          </Svg>
        </Chart>
      </div>

      <!-- IOB/COB Chart -->
      <div class="h-[60px] mt-1">
        <Chart
          data={iobData}
          x={(d) => d.time}
          y={(d) => d.iob}
          xScale={scaleTime()}
          yScale={scaleLinear()}
          xDomain={[displayDateRange.from, displayDateRange.to]}
          yDomain={[0, maxIOB]}
          padding={{ left: 48, bottom: 0, top: 4, right: 12 }}
        >
          <Svg>
            <ChartClipPath>
              <!-- IOB area -->
              {#if iobData.some((d) => d.iob > 0.01)}
                <Area
                  y0={0}
                  curve={curveMonotoneX}
                  class="fill-blue-600/30 stroke-blue-400 stroke-1"
                />
              {/if}
            </ChartClipPath>

            <!-- Label -->
            <Text x={4} y={4} class="text-[8px] fill-slate-500 font-medium">
              IOB
            </Text>
            {#if maxIOB >= 1}
              <Text
                x={98}
                y={(1 / maxIOB) * 100}
                textAnchor="end"
                class="text-[6px] fill-blue-400"
              >
                1U
              </Text>
            {/if}
          </Svg>
        </Chart>
      </div>
    </div>

    <!-- Legend -->
    <div
      class="flex flex-wrap justify-center gap-4 text-[10px] text-slate-500 pt-2"
    >
      <div class="flex items-center gap-1">
        <div class="w-2 h-2 rounded-full bg-green-500"></div>
        <span>In Range</span>
      </div>
      <div class="flex items-center gap-1">
        <div class="w-2 h-2 rounded-full bg-amber-500"></div>
        <span>High</span>
      </div>
      <div class="flex items-center gap-1">
        <div class="w-2 h-2 rounded-full bg-red-500"></div>
        <span>Low</span>
      </div>
      <div class="flex items-center gap-1">
        <div class="w-3 h-2 bg-blue-500/50 border border-blue-400"></div>
        <span>Basal</span>
      </div>
      <div class="flex items-center gap-1">
        <div class="w-3 h-2 bg-blue-600/40 border border-blue-400"></div>
        <span>IOB</span>
      </div>
      <div class="flex items-center gap-1">
        <div
          class="w-0 h-0 border-l-4 border-r-4 border-b-4 border-l-transparent border-r-transparent border-b-blue-500"
        ></div>
        <span>Bolus</span>
      </div>
      <div class="flex items-center gap-1">
        <div
          class="w-0 h-0 border-l-4 border-r-4 border-t-4 border-l-transparent border-r-transparent border-t-amber-500"
        ></div>
        <span>Carbs</span>
      </div>
    </div>
  </CardContent>
</Card>
