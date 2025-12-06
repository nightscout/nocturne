<script lang="ts">
  import { Chart, Svg, Axis, Spline, Rect, Group, Points } from "layerchart";
  import { scaleTime, scaleLinear } from "d3-scale";
  import { goto } from "$app/navigation";
  import { page } from "$app/state";
  import type { Entry, Treatment, TreatmentSummary } from "$lib/api";
  import { DEFAULT_THRESHOLDS } from "$lib/constants";
  import { TIR_COLORS_HEX } from "$lib/constants/tir-colors";
  import * as Card from "$lib/components/ui/card";
  import * as Table from "$lib/components/ui/table";
  import Button from "$lib/components/ui/button/button.svelte";
  import {
    ChevronLeft,
    ChevronRight,
    Calendar,
    ArrowLeft,
    Pill,
    Apple,
    Droplet,
    Activity,
    Target,
  } from "lucide-svelte";
  import { getDayInReviewData } from "./data.remote";

  // Get date from URL search params
  const dateParam = $derived(
    page.url.searchParams.get("date") ?? new Date().toISOString().split("T")[0]
  );

  // Fetch data using remote function
  const dayData = $derived(await getDayInReviewData(dateParam));

  // Parse current date from URL
  const currentDate = $derived(new Date(dateParam));

  // Date navigation
  function goToPreviousDay() {
    const prevDate = new Date(currentDate);
    prevDate.setDate(prevDate.getDate() - 1);
    goto(
      `/reports/day-in-review?date=${prevDate.toISOString().split("T")[0]}`,
      {
        invalidateAll: true,
      }
    );
  }

  function goToNextDay() {
    const nextDate = new Date(currentDate);
    nextDate.setDate(nextDate.getDate() + 1);
    goto(
      `/reports/day-in-review?date=${nextDate.toISOString().split("T")[0]}`,
      {
        invalidateAll: true,
      }
    );
  }

  function goBackToMonthView() {
    goto("/calendar");
  }

  // Format date for display
  const dateDisplay = $derived.by(() => {
    return currentDate.toLocaleDateString(undefined, {
      weekday: "long",
      year: "numeric",
      month: "long",
      day: "numeric",
    });
  });

  // Colors for glucose chart
  const GLUCOSE_COLORS = {
    line: "#1e90ff", // Blue
    low: TIR_COLORS_HEX.low,
    inRange: TIR_COLORS_HEX.target,
    high: TIR_COLORS_HEX.high,
    severeLow: TIR_COLORS_HEX.severeLow,
    severeHigh: TIR_COLORS_HEX.severeHigh,
  };

  // Treatment colors
  const TREATMENT_COLORS = {
    carbs: "#ff9a00", // Orange
    insulin: "#0066cc", // Dark Blue
    bolus: "#0099ff", // Light Blue
    basal: "#66ccff", // Lighter Blue
  };

  // Thresholds
  const lowThreshold = DEFAULT_THRESHOLDS.low ?? 70;
  const highThreshold = DEFAULT_THRESHOLDS.high ?? 180;
  const targetBottom = DEFAULT_THRESHOLDS.targetBottom ?? 70;
  const targetTop = DEFAULT_THRESHOLDS.targetTop ?? 180;

  // Process entries for the chart
  const chartData = $derived.by(() => {
    const entries = dayData.entries as Entry[];

    return entries
      .filter((e) => (e.sgv || e.mgdl) && e.mills)
      .map((e) => ({
        time: new Date(e.mills!),
        glucose: e.sgv ?? e.mgdl ?? 0,
        direction: e.direction,
      }))
      .sort((a, b) => a.time.getTime() - b.time.getTime());
  });

  // Process treatments for markers
  const treatmentMarkers = $derived.by(() => {
    const treatments = dayData.treatments as Treatment[];

    return treatments
      .filter((t) => t.mills || t.createdAt)
      .map((t) => ({
        time: new Date(t.mills ?? new Date(t.createdAt!).getTime()),
        carbs: t.carbs ?? 0,
        insulin: t.insulin ?? 0,
        eventType: t.eventType ?? "",
        notes: t.notes ?? "",
        rate: t.rate,
        duration: t.duration,
      }))
      .sort((a, b) => a.time.getTime() - b.time.getTime());
  });

  // X-axis domain (full 24-hour period)
  const xDomain: [Date, Date] = $derived([
    new Date(
      currentDate.getFullYear(),
      currentDate.getMonth(),
      currentDate.getDate(),
      0,
      0,
      0
    ),
    new Date(
      currentDate.getFullYear(),
      currentDate.getMonth(),
      currentDate.getDate(),
      23,
      59,
      59
    ),
  ]);

  // Y-axis domain based on data
  const yDomain: [number, number] = $derived.by(() => {
    if (chartData.length === 0) return [40, 300];

    const values = chartData.map((d) => d.glucose);
    const minY = Math.min(...values, lowThreshold);
    const maxY = Math.max(...values, highThreshold);

    return [Math.max(20, minY - 20), maxY + 40];
  });

  // Calculate glucose statistics
  const glucoseStats = $derived.by(() => {
    const readings = chartData.map((d) => d.glucose);

    if (readings.length === 0) {
      return {
        totalReadings: 0,
        mean: 0,
        median: 0,
        stdDev: 0,
        min: 0,
        max: 0,
        inRange: 0,
        low: 0,
        high: 0,
        inRangePercent: 0,
        lowPercent: 0,
        highPercent: 0,
        a1cEstimate: 0,
      };
    }

    const totalReadings = readings.length;
    const mean = readings.reduce((a, b) => a + b, 0) / totalReadings;

    const sorted = [...readings].sort((a, b) => a - b);
    const median =
      totalReadings % 2 === 0
        ? (sorted[totalReadings / 2 - 1] + sorted[totalReadings / 2]) / 2
        : sorted[Math.floor(totalReadings / 2)];

    const stdDev = Math.sqrt(
      readings.reduce((sum, val) => sum + Math.pow(val - mean, 2), 0) /
        totalReadings
    );

    const min = Math.min(...readings);
    const max = Math.max(...readings);

    const low = readings.filter((r) => r < lowThreshold).length;
    const high = readings.filter((r) => r >= highThreshold).length;
    const inRange = totalReadings - low - high;

    const inRangePercent = (inRange / totalReadings) * 100;
    const lowPercent = (low / totalReadings) * 100;
    const highPercent = (high / totalReadings) * 100;

    // A1c estimate (DCCT formula)
    const a1cEstimate = (mean + 46.7) / 28.7;

    return {
      totalReadings,
      mean,
      median,
      stdDev,
      min,
      max,
      inRange,
      low,
      high,
      inRangePercent,
      lowPercent,
      highPercent,
      a1cEstimate,
    };
  });

  const treatmentStats = $derived.by(() => {
    const summary = dayData.treatmentSummary as TreatmentSummary | null;
    const totalBolus = summary?.totals?.insulin?.bolus ?? 0;
    const totalBasal = summary?.totals?.insulin?.basal ?? 0;
    const totalCarbs = summary?.totals?.food?.carbs ?? 0;
    const totalInsulin = totalBolus + totalBasal;

    // Count treatments with bolus/carbs for display
    const treatments = dayData.treatments as Treatment[];
    let bolusCount = 0;
    let carbCount = 0;
    for (const treatment of treatments) {
      if (treatment.carbs && treatment.carbs > 0) {
        carbCount++;
      }
      if (treatment.insulin && treatment.insulin > 0) {
        const eventType = treatment.eventType?.toLowerCase() ?? "";
        if (!eventType.includes("basal") && !eventType.includes("temp")) {
          bolusCount++;
        }
      }
    }

    return {
      totalCarbs,
      totalBolus,
      totalBasal,
      totalInsulin,
      bolusCount,
      carbCount,
      positiveBasalTemp: 0, // Not calculated on frontend
      negativeBasalTemp: 0,
      baseBasal: totalBasal * 0.8, // Approximate
      totalDailyInsulin: totalInsulin,
    };
  });

  // Pie chart data for TIR distribution
  const tirPieData = $derived(
    [
      {
        name: "In Range",
        value: glucoseStats.inRangePercent,
        color: GLUCOSE_COLORS.inRange,
      },
      {
        name: "Low",
        value: glucoseStats.lowPercent,
        color: GLUCOSE_COLORS.low,
      },
      {
        name: "High",
        value: glucoseStats.highPercent,
        color: GLUCOSE_COLORS.high,
      },
    ].filter((d) => d.value > 0)
  );

  // Format time for axis labels
  function formatTime(date: Date): string {
    const hours = date.getHours();
    return hours === 0
      ? "12a"
      : hours === 12
        ? "12p"
        : hours > 12
          ? `${hours - 12}p`
          : `${hours}a`;
  }

  // Get color for glucose value
  function getGlucoseColor(value: number): string {
    if (value < DEFAULT_THRESHOLDS.severeLow!) return GLUCOSE_COLORS.severeLow;
    if (value < lowThreshold) return GLUCOSE_COLORS.low;
    if (value >= (DEFAULT_THRESHOLDS.severeHigh ?? 250))
      return GLUCOSE_COLORS.severeHigh;
    if (value >= highThreshold) return GLUCOSE_COLORS.high;
    return GLUCOSE_COLORS.inRange;
  }
</script>

<div class="space-y-6 p-4">
  <!-- Header with Navigation -->
  <Card.Root>
    <Card.Content class="p-4">
      <div class="flex flex-wrap items-center justify-between gap-4">
        <!-- Back button -->
        <Button variant="ghost" size="sm" onclick={goBackToMonthView}>
          <ArrowLeft class="h-4 w-4 mr-2" />
          Back to Month View
        </Button>

        <!-- Date Navigation -->
        <div class="flex items-center gap-2">
          <Button variant="outline" size="icon" onclick={goToPreviousDay}>
            <ChevronLeft class="h-4 w-4" />
          </Button>
          <div class="flex items-center gap-2 min-w-[280px] justify-center">
            <Calendar class="h-4 w-4 text-muted-foreground" />
            <span class="text-lg font-medium">{dateDisplay}</span>
          </div>
          <Button variant="outline" size="icon" onclick={goToNextDay}>
            <ChevronRight class="h-4 w-4" />
          </Button>
        </div>

        <div class="w-[100px]"><!-- Spacer for alignment --></div>
      </div>
    </Card.Content>
  </Card.Root>

  <!-- Summary Stats Row -->
  <div class="grid grid-cols-2 md:grid-cols-4 lg:grid-cols-6 gap-4">
    <!-- Time in Range -->
    <Card.Root>
      <Card.Content class="p-4 text-center">
        <div class="text-3xl font-bold" style="color: {GLUCOSE_COLORS.inRange}">
          {glucoseStats.inRangePercent.toFixed(0)}%
        </div>
        <div class="text-sm text-muted-foreground">Time in Range</div>
      </Card.Content>
    </Card.Root>

    <!-- Low -->
    <Card.Root>
      <Card.Content class="p-4 text-center">
        <div class="text-3xl font-bold" style="color: {GLUCOSE_COLORS.low}">
          {glucoseStats.lowPercent.toFixed(0)}%
        </div>
        <div class="text-sm text-muted-foreground">Time Low</div>
      </Card.Content>
    </Card.Root>

    <!-- High -->
    <Card.Root>
      <Card.Content class="p-4 text-center">
        <div class="text-3xl font-bold" style="color: {GLUCOSE_COLORS.high}">
          {glucoseStats.highPercent.toFixed(0)}%
        </div>
        <div class="text-sm text-muted-foreground">Time High</div>
      </Card.Content>
    </Card.Root>

    <!-- Total Carbs -->
    <Card.Root>
      <Card.Content class="p-4 text-center">
        <div class="text-3xl font-bold" style="color: {TREATMENT_COLORS.carbs}">
          {treatmentStats.totalCarbs.toFixed(0)}g
        </div>
        <div class="text-sm text-muted-foreground">Total Carbs</div>
      </Card.Content>
    </Card.Root>

    <!-- Total Bolus -->
    <Card.Root>
      <Card.Content class="p-4 text-center">
        <div class="text-3xl font-bold" style="color: {TREATMENT_COLORS.bolus}">
          {treatmentStats.totalBolus.toFixed(1)}U
        </div>
        <div class="text-sm text-muted-foreground">Bolus Insulin</div>
      </Card.Content>
    </Card.Root>

    <!-- Total Daily Insulin -->
    <Card.Root>
      <Card.Content class="p-4 text-center">
        <div
          class="text-3xl font-bold"
          style="color: {TREATMENT_COLORS.insulin}"
        >
          {treatmentStats.totalDailyInsulin.toFixed(1)}U
        </div>
        <div class="text-sm text-muted-foreground">Total Daily Insulin</div>
      </Card.Content>
    </Card.Root>
  </div>

  <!-- Main Glucose Chart -->
  <Card.Root>
    <Card.Header class="pb-2">
      <Card.Title class="flex items-center gap-2">
        <Activity class="h-5 w-5" />
        Glucose Profile
      </Card.Title>
    </Card.Header>
    <Card.Content>
      <div class="h-[400px]">
        {#if chartData.length > 0}
          <Chart
            data={chartData}
            x={(d) => d.time}
            y={(d) => d.glucose}
            xScale={scaleTime()}
            yScale={scaleLinear()}
            {xDomain}
            {yDomain}
            padding={{ top: 20, right: 30, bottom: 40, left: 50 }}
          >
            <Svg>
              <!-- Axes -->
              <Axis placement="left" rule label="mg/dL" />
              <Axis
                placement="bottom"
                rule
                format={formatTime}
                ticks={[0, 3, 6, 9, 12, 15, 18, 21].map(
                  (h) =>
                    new Date(
                      currentDate.getFullYear(),
                      currentDate.getMonth(),
                      currentDate.getDate(),
                      h
                    )
                )}
              />

              <!-- Target range background -->
              <Group class="target-range">
                <Rect
                  x={0}
                  y={targetTop}
                  width={100}
                  height={targetTop - targetBottom}
                  class="fill-green-500/20"
                />
              </Group>

              <!-- Low threshold line -->
              <line
                x1="0%"
                x2="100%"
                y1={lowThreshold}
                y2={lowThreshold}
                class="stroke-red-500/50 stroke-dashed"
                stroke-dasharray="4,4"
              />

              <!-- High threshold line -->
              <line
                x1="0%"
                x2="100%"
                y1={highThreshold}
                y2={highThreshold}
                class="stroke-yellow-500/50 stroke-dashed"
                stroke-dasharray="4,4"
              />

              <!-- Glucose line -->
              <Spline
                data={chartData}
                x={(d) => d.time}
                y={(d) => d.glucose}
                stroke={GLUCOSE_COLORS.line}
                stroke-width={2}
              />

              <!-- Glucose points with color based on value -->
              {#each chartData as point}
                <Points
                  data={[point]}
                  x={(d) => d.time}
                  y={(d) => d.glucose}
                  r={3}
                  fill={getGlucoseColor(point.glucose)}
                />
              {/each}
            </Svg>
          </Chart>
        {:else}
          <div
            class="flex h-full items-center justify-center text-muted-foreground"
          >
            No glucose data available for this day
          </div>
        {/if}
      </div>
    </Card.Content>
  </Card.Root>

  <!-- Detailed Stats Row -->
  <div class="grid md:grid-cols-3 gap-6">
    <!-- TIR Distribution Pie -->
    <Card.Root>
      <Card.Header class="pb-2">
        <Card.Title class="flex items-center gap-2">
          <Target class="h-5 w-5" />
          Glucose Distribution
        </Card.Title>
      </Card.Header>
      <Card.Content>
        <div class="h-[200px] flex items-center justify-center">
          {#if tirPieData.length > 0}
            {@const radius = 80}
            {@const innerRadius = 50}
            <svg
              width={radius * 2 + 20}
              height={radius * 2 + 20}
              viewBox="-{radius + 10} -{radius + 10} {radius * 2 + 20} {radius *
                2 +
                20}"
            >
              {#each tirPieData as slice, i}
                {@const total = tirPieData.reduce((sum, d) => sum + d.value, 0)}
                {@const startAngle =
                  tirPieData
                    .slice(0, i)
                    .reduce((sum, d) => sum + (d.value / total) * 360, 0) - 90}
                {@const endAngle = startAngle + (slice.value / total) * 360}
                {@const startRad = (startAngle * Math.PI) / 180}
                {@const endRad = (endAngle * Math.PI) / 180}
                {@const largeArc = endAngle - startAngle > 180 ? 1 : 0}
                {@const x1Outer = radius * Math.cos(startRad)}
                {@const y1Outer = radius * Math.sin(startRad)}
                {@const x2Outer = radius * Math.cos(endRad)}
                {@const y2Outer = radius * Math.sin(endRad)}
                {@const x1Inner = innerRadius * Math.cos(endRad)}
                {@const y1Inner = innerRadius * Math.sin(endRad)}
                {@const x2Inner = innerRadius * Math.cos(startRad)}
                {@const y2Inner = innerRadius * Math.sin(startRad)}
                <path
                  d="M {x1Outer} {y1Outer} A {radius} {radius} 0 {largeArc} 1 {x2Outer} {y2Outer} L {x1Inner} {y1Inner} A {innerRadius} {innerRadius} 0 {largeArc} 0 {x2Inner} {y2Inner} Z"
                  fill={slice.color}
                  stroke="white"
                  stroke-width="2"
                />
              {/each}
            </svg>
          {:else}
            <span class="text-muted-foreground">No data</span>
          {/if}
        </div>

        <!-- Legend -->
        <div class="flex justify-center gap-4 mt-4">
          {#each tirPieData as slice}
            <div class="flex items-center gap-2">
              <div
                class="h-3 w-3 rounded-full"
                style="background-color: {slice.color}"
              ></div>
              <span class="text-sm">
                {slice.name}: {slice.value.toFixed(1)}%
              </span>
            </div>
          {/each}
        </div>
      </Card.Content>
    </Card.Root>

    <!-- Glucose Statistics -->
    <Card.Root>
      <Card.Header class="pb-2">
        <Card.Title class="flex items-center gap-2">
          <Droplet class="h-5 w-5" />
          Glucose Statistics
        </Card.Title>
      </Card.Header>
      <Card.Content>
        <Table.Root>
          <Table.Body>
            <Table.Row>
              <Table.Cell class="font-medium">Mean</Table.Cell>
              <Table.Cell class="text-right">
                {glucoseStats.mean.toFixed(0)} mg/dL
              </Table.Cell>
            </Table.Row>
            <Table.Row>
              <Table.Cell class="font-medium">Median</Table.Cell>
              <Table.Cell class="text-right">
                {glucoseStats.median.toFixed(0)} mg/dL
              </Table.Cell>
            </Table.Row>
            <Table.Row>
              <Table.Cell class="font-medium">Std Dev</Table.Cell>
              <Table.Cell class="text-right">
                {glucoseStats.stdDev.toFixed(1)} mg/dL
              </Table.Cell>
            </Table.Row>
            <Table.Row>
              <Table.Cell class="font-medium">Min / Max</Table.Cell>
              <Table.Cell class="text-right">
                {glucoseStats.min} / {glucoseStats.max} mg/dL
              </Table.Cell>
            </Table.Row>
            <Table.Row>
              <Table.Cell class="font-medium">Readings</Table.Cell>
              <Table.Cell class="text-right">
                {glucoseStats.totalReadings}
              </Table.Cell>
            </Table.Row>
            <Table.Row>
              <Table.Cell class="font-medium">Est. A1c</Table.Cell>
              <Table.Cell class="text-right">
                {glucoseStats.a1cEstimate.toFixed(1)}%
              </Table.Cell>
            </Table.Row>
          </Table.Body>
        </Table.Root>
      </Card.Content>
    </Card.Root>

    <!-- Insulin Statistics -->
    <Card.Root>
      <Card.Header class="pb-2">
        <Card.Title class="flex items-center gap-2">
          <Pill class="h-5 w-5" />
          Insulin & Carbs
        </Card.Title>
      </Card.Header>
      <Card.Content>
        <Table.Root>
          <Table.Body>
            <Table.Row>
              <Table.Cell class="font-medium">Bolus Insulin</Table.Cell>
              <Table.Cell class="text-right">
                {treatmentStats.totalBolus.toFixed(1)}U
              </Table.Cell>
            </Table.Row>
            <Table.Row>
              <Table.Cell class="font-medium">Basal Insulin</Table.Cell>
              <Table.Cell class="text-right">
                {treatmentStats.totalBasal.toFixed(1)}U
              </Table.Cell>
            </Table.Row>
            <Table.Row>
              <Table.Cell class="font-medium">Total Daily Insulin</Table.Cell>
              <Table.Cell class="text-right font-bold">
                {treatmentStats.totalDailyInsulin.toFixed(1)}U
              </Table.Cell>
            </Table.Row>
            <Table.Row>
              <Table.Cell class="font-medium">Bolus Count</Table.Cell>
              <Table.Cell class="text-right">
                {treatmentStats.bolusCount}
              </Table.Cell>
            </Table.Row>
            <Table.Row>
              <Table.Cell class="font-medium">Total Carbs</Table.Cell>
              <Table.Cell class="text-right font-bold">
                {treatmentStats.totalCarbs.toFixed(0)}g
              </Table.Cell>
            </Table.Row>
            <Table.Row>
              <Table.Cell class="font-medium">Carb Entries</Table.Cell>
              <Table.Cell class="text-right">
                {treatmentStats.carbCount}
              </Table.Cell>
            </Table.Row>
          </Table.Body>
        </Table.Root>
      </Card.Content>
    </Card.Root>
  </div>

  <!-- Treatments Timeline -->
  <Card.Root>
    <Card.Header class="pb-2">
      <Card.Title class="flex items-center gap-2">
        <Apple class="h-5 w-5" />
        Treatments Timeline
      </Card.Title>
    </Card.Header>
    <Card.Content>
      {#if treatmentMarkers.length > 0}
        <Table.Root>
          <Table.Header>
            <Table.Row>
              <Table.Head>Time</Table.Head>
              <Table.Head>Type</Table.Head>
              <Table.Head class="text-right">Carbs</Table.Head>
              <Table.Head class="text-right">Insulin</Table.Head>
              <Table.Head>Notes</Table.Head>
            </Table.Row>
          </Table.Header>
          <Table.Body>
            {#each treatmentMarkers as treatment}
              <Table.Row>
                <Table.Cell class="font-medium">
                  {treatment.time.toLocaleTimeString(undefined, {
                    hour: "2-digit",
                    minute: "2-digit",
                  })}
                </Table.Cell>
                <Table.Cell>
                  <span class="px-2 py-1 rounded text-xs font-medium bg-muted">
                    {treatment.eventType || "—"}
                  </span>
                </Table.Cell>
                <Table.Cell class="text-right">
                  {#if treatment.carbs > 0}
                    <span style="color: {TREATMENT_COLORS.carbs}">
                      {treatment.carbs}g
                    </span>
                  {:else}
                    —
                  {/if}
                </Table.Cell>
                <Table.Cell class="text-right">
                  {#if treatment.insulin > 0}
                    <span style="color: {TREATMENT_COLORS.bolus}">
                      {treatment.insulin.toFixed(2)}U
                    </span>
                  {:else if treatment.rate !== undefined}
                    <span style="color: {TREATMENT_COLORS.basal}">
                      {treatment.rate.toFixed(2)}U/hr
                    </span>
                  {:else}
                    —
                  {/if}
                </Table.Cell>
                <Table.Cell
                  class="text-muted-foreground truncate max-w-[200px]"
                >
                  {treatment.notes || "—"}
                </Table.Cell>
              </Table.Row>
            {/each}
          </Table.Body>
        </Table.Root>
      {:else}
        <p class="text-center text-muted-foreground py-8">
          No treatments recorded for this day
        </p>
      {/if}
    </Card.Content>
  </Card.Root>
</div>
