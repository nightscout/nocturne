<script lang="ts">
  import { goto } from "$app/navigation";
  import { page } from "$app/state";
  import * as Card from "$lib/components/ui/card";
  import Button from "$lib/components/ui/button/button.svelte";
  import DateRangePicker from "$lib/components/ui/date-range-picker.svelte";
  import { ChevronLeft, ChevronRight, ArrowLeft } from "lucide-svelte";
  import { getTimeSpansData } from "./data.remote";
  import { Chart, Svg, Axis, Bars, Spline, Highlight } from "layerchart";
  import { scaleTime, scaleBand } from "d3-scale";
  import { curveMonotoneX } from "d3";
  import { bg } from "$lib/utils/formatting";
  import { chartConfig } from "$lib/constants";

  // Get date range from URL search params
  const fromParam = $derived(
    page.url.searchParams.get("from") ??
      new Date(Date.now() - 6 * 24 * 60 * 60 * 1000).toISOString().split("T")[0]
  );
  const toParam = $derived(
    page.url.searchParams.get("to") ?? new Date().toISOString().split("T")[0]
  );

  // Fetch data using remote function with date range
  const dataQuery = $derived(
    getTimeSpansData({ from: fromParam, to: toParam })
  );
  const data = $derived(dataQuery.current);

  // Parse dates for display and navigation
  const fromDate = $derived(new Date(fromParam));
  const toDate = $derived(new Date(toParam));

  // Calculate number of days in range for chart sizing
  const dayCount = $derived(
    Math.max(
      1,
      Math.ceil(
        (toDate.getTime() - fromDate.getTime()) / (24 * 60 * 60 * 1000)
      ) + 1
    )
  );

  // Date navigation - shift by the current range duration
  function goToPreviousPeriod() {
    const durationMs = toDate.getTime() - fromDate.getTime();
    const newTo = new Date(fromDate.getTime() - 24 * 60 * 60 * 1000);
    const newFrom = new Date(newTo.getTime() - durationMs);
    goto(
      `/time-spans?from=${newFrom.toISOString().split("T")[0]}&to=${newTo.toISOString().split("T")[0]}`,
      { invalidateAll: true }
    );
  }

  function goToNextPeriod() {
    const durationMs = toDate.getTime() - fromDate.getTime();
    const newFrom = new Date(toDate.getTime() + 24 * 60 * 60 * 1000);
    const newTo = new Date(newFrom.getTime() + durationMs);
    goto(
      `/time-spans?from=${newFrom.toISOString().split("T")[0]}&to=${newTo.toISOString().split("T")[0]}`,
      { invalidateAll: true }
    );
  }

  function goBack() {
    goto("/dashboard");
  }

  // Format date range for display
  const dateRangeDisplay = $derived.by(() => {
    if (dayCount === 1) {
      return fromDate.toLocaleDateString(undefined, {
        weekday: "long",
        year: "numeric",
        month: "long",
        day: "numeric",
      });
    }
    return `${fromDate.toLocaleDateString(undefined, {
      month: "short",
      day: "numeric",
    })} - ${toDate.toLocaleDateString(undefined, {
      month: "short",
      day: "numeric",
      year: "numeric",
    })} (${dayCount} days)`;
  });

  // Row definitions for the chart y-axis
  const rowCategories = [
    "Pump Mode",
    "Profile",
    "Temp Basal",
    "Override",
  ] as const;
  type RowCategory = (typeof rowCategories)[number];

  // Date range for x-axis
  const dateRange = $derived({
    from: data?.dateRange.from ?? fromDate,
    to: data?.dateRange.to ?? toDate,
  });

  // Transform all spans into a flat array with category info for the bar chart
  interface SpanBarData {
    id: string;
    name: RowCategory;
    startDate: Date;
    endDate: Date;
    state: string;
    color: string;
  }

  const barData = $derived.by((): SpanBarData[] => {
    if (!data) return [];

    const allSpans: SpanBarData[] = [];

    // Add pump mode spans
    data.pumpModeSpans.forEach((span) => {
      allSpans.push({
        id: span.id,
        name: "Pump Mode",
        startDate: span.startTime,
        endDate: span.endTime,
        state: span.state,
        color: span.color,
      });
    });

    // Add profile spans
    data.profileSpans.forEach((span) => {
      allSpans.push({
        id: span.id,
        name: "Profile",
        startDate: span.startTime,
        endDate: span.endTime,
        state: span.state,
        color: span.color,
      });
    });

    // Add temp basal spans
    data.tempBasalSpans.forEach((span) => {
      allSpans.push({
        id: span.id,
        name: "Temp Basal",
        startDate: span.startTime,
        endDate: span.endTime,
        state: span.state,
        color: span.color,
      });
    });

    // Add override spans
    data.overrideSpans.forEach((span) => {
      allSpans.push({
        id: span.id,
        name: "Override",
        startDate: span.startTime,
        endDate: span.endTime,
        state: span.state,
        color: span.color,
      });
    });

    return allSpans;
  });

  // Glucose data for overlay
  const glucoseData = $derived(
    (data?.entries ?? [])
      .filter((e) => e.sgv !== null && e.sgv !== undefined)
      .map((e) => ({
        time: new Date(e.mills ?? 0),
        sgv: Number(bg(e.sgv ?? 0)),
        color: getGlucoseColor(e.sgv ?? 0),
      }))
      .sort((a, b) => a.time.getTime() - b.time.getTime())
  );

  // Glucose color helper
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

  // Format time for display
  function formatTime(date: Date): string {
    if (dayCount > 3) {
      return date.toLocaleDateString([], {
        month: "numeric",
        day: "numeric",
      });
    }
    return date.toLocaleTimeString([], { hour: "numeric", minute: "2-digit" });
  }

  // Render context for custom bar colors
  const renderContext = {
    each: (
      items: SpanBarData[],
      rect: (
        item: SpanBarData,
        index: number,
        items: SpanBarData[]
      ) => Record<string, unknown>
    ) => {
      return items.map((item, index) => ({
        ...rect(item, index, items),
        fill: item.color,
        class: "opacity-80 hover:opacity-100 transition-opacity",
      }));
    },
  };
</script>

<div class="space-y-6 p-4">
  <!-- Header with Navigation -->
  <Card.Root>
    <Card.Content class="p-4">
      <div class="flex flex-wrap items-center justify-between gap-4">
        <!-- Back button -->
        <Button variant="ghost" size="sm" onclick={goBack}>
          <ArrowLeft class="h-4 w-4 mr-2" />
          Back to Dashboard
        </Button>

        <!-- Date Navigation -->
        <div class="flex items-center gap-2">
          <Button variant="outline" size="icon" onclick={goToPreviousPeriod}>
            <ChevronLeft class="h-4 w-4" />
          </Button>
          <div
            class="flex items-center gap-2 min-w-[280px] justify-center text-center"
          >
            <span class="text-lg font-medium">{dateRangeDisplay}</span>
          </div>
          <Button variant="outline" size="icon" onclick={goToNextPeriod}>
            <ChevronRight class="h-4 w-4" />
          </Button>
        </div>

        <div class="w-24"></div>
      </div>
    </Card.Content>
  </Card.Root>

  <!-- Date Range Picker -->
  <DateRangePicker
    title="Select Date Range"
    showDaysPresets={true}
    defaultDays={7}
  />

  <!-- Time Spans Chart -->
  <Card.Root>
    <Card.Header class="pb-2">
      <Card.Title>Time Spans</Card.Title>
      <Card.Description>
        State changes throughout the selected period
      </Card.Description>
    </Card.Header>
    <Card.Content>
      <div class="h-[350px] w-full">
        {#if data && barData.length > 0}
          <Chart
            data={barData}
            x={["startDate", "endDate"]}
            xScale={scaleTime()}
            xDomain={[dateRange.from, dateRange.to]}
            y="name"
            yScale={scaleBand().padding(0.3)}
            yDomain={[...rowCategories]}
            padding={{ left: 90, bottom: 40, top: 20, right: 20 }}
          >
            {#snippet children()}
              <Svg>
                <Axis
                  placement="left"
                  tickLabelProps={{ class: "text-xs fill-muted-foreground" }}
                />
                <Axis
                  placement="bottom"
                  format={(v) =>
                    v instanceof Date ? formatTime(v) : String(v)}
                  tickLabelProps={{ class: "text-xs fill-muted-foreground" }}
                />
                <Bars />
                <Highlight area />
              </Svg>
            {/snippet}
          </Chart>
        {:else if data}
          <div class="flex h-full items-center justify-center">
            <p class="text-muted-foreground">
              No state spans found for this period
            </p>
          </div>
        {:else}
          <div class="flex h-full items-center justify-center">
            <p class="text-muted-foreground">Loading...</p>
          </div>
        {/if}
      </div>
    </Card.Content>
  </Card.Root>

  <!-- Glucose Chart (separate) -->
  <Card.Root>
    <Card.Header class="pb-2">
      <Card.Title>Glucose Overlay</Card.Title>
      <Card.Description>
        Blood glucose readings during this period
      </Card.Description>
    </Card.Header>
    <Card.Content>
      <div class="h-[200px] w-full">
        {#if glucoseData.length > 0}
          <Chart
            data={glucoseData}
            x={(d) => d.time}
            y="sgv"
            xScale={scaleTime()}
            xDomain={[dateRange.from, dateRange.to]}
            yDomain={[0, 350]}
            padding={{ left: 50, bottom: 40, top: 10, right: 20 }}
          >
            {#snippet children()}
              <Svg>
                <Axis
                  placement="left"
                  ticks={4}
                  tickLabelProps={{ class: "text-xs fill-muted-foreground" }}
                />
                <Axis
                  placement="bottom"
                  format={(v) =>
                    v instanceof Date ? formatTime(v) : String(v)}
                  tickLabelProps={{ class: "text-xs fill-muted-foreground" }}
                />
                <Spline
                  data={glucoseData}
                  x={(d) => d.time}
                  y={(d) => d.sgv}
                  curve={curveMonotoneX}
                  class="stroke-glucose-in-range stroke-2 fill-none"
                />
              </Svg>
            {/snippet}
          </Chart>
        {:else}
          <div class="flex h-full items-center justify-center">
            <p class="text-muted-foreground">No glucose data</p>
          </div>
        {/if}
      </div>
    </Card.Content>
  </Card.Root>

  <!-- Legend -->
  <Card.Root>
    <Card.Header class="pb-2">
      <Card.Title class="text-sm">Legend</Card.Title>
    </Card.Header>
    <Card.Content>
      <div class="grid grid-cols-2 md:grid-cols-4 gap-4">
        <!-- Pump Modes -->
        <div class="space-y-2">
          <h4 class="text-xs font-medium text-muted-foreground">Pump Modes</h4>
          <div class="flex flex-wrap gap-2">
            {#each ["Automatic", "Limited", "Manual", "Suspended"] as mode}
              <div class="flex items-center gap-1">
                <div
                  class="w-3 h-3 rounded"
                  style="background-color: var(--pump-mode-{mode.toLowerCase()})"
                ></div>
                <span class="text-xs">{mode}</span>
              </div>
            {/each}
          </div>
        </div>

        <!-- Overrides -->
        <div class="space-y-2">
          <h4 class="text-xs font-medium text-muted-foreground">Overrides</h4>
          <div class="flex flex-wrap gap-2">
            {#each ["Boost", "Exercise", "Sleep", "EaseOff"] as mode}
              <div class="flex items-center gap-1">
                <div
                  class="w-3 h-3 rounded"
                  style="background-color: var(--pump-mode-{mode
                    .toLowerCase()
                    .replace('easeoff', 'ease-off')})"
                ></div>
                <span class="text-xs">{mode}</span>
              </div>
            {/each}
          </div>
        </div>

        <!-- Temp Basal -->
        <div class="space-y-2">
          <h4 class="text-xs font-medium text-muted-foreground">Temp Basal</h4>
          <div class="flex items-center gap-1">
            <div
              class="w-3 h-3 rounded"
              style="background-color: var(--insulin-basal)"
            ></div>
            <span class="text-xs">Active Temp Basal</span>
          </div>
        </div>

        <!-- Glucose -->
        <div class="space-y-2">
          <h4 class="text-xs font-medium text-muted-foreground">
            Glucose Line
          </h4>
          <div class="flex items-center gap-1">
            <div
              class="w-8 h-0.5 rounded"
              style="background-color: var(--glucose-in-range)"
            ></div>
            <span class="text-xs">Blood Glucose</span>
          </div>
        </div>
      </div>
    </Card.Content>
  </Card.Root>
</div>
