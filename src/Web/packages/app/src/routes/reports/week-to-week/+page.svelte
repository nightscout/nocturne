<script lang="ts">
  import {
    Chart,
    Axis,
    Svg,
    Spline,
    Rect,
    Group,
    Points,
    Legend,
  } from "layerchart";
  import { scaleTime, scaleLinear, scaleLog } from "d3-scale";
  import { goto } from "$app/navigation";
  import { page } from "$app/state";
  import type { Entry } from "$lib/api";
  import { DEFAULT_THRESHOLDS } from "$lib/constants";
  import * as Select from "$lib/components/ui/select";
  import * as Card from "$lib/components/ui/card";
  import Button from "$lib/components/ui/button/button.svelte";
  import { ChevronLeft, ChevronRight, Calendar } from "lucide-svelte";
  import {
    getPointInTimeData,
    type PointInTimeData,
  } from "$lib/data/week-to-week.remote";
  import PointDetailDialog from "$lib/components/reports/PointDetailDialog.svelte";
  import { getReportsData } from "$lib/data/reports.remote";
  import { useDateRange } from "$lib/hooks/use-date-range.svelte.js";
  import {
    glucoseUnits,
    timeFormat,
  } from "$lib/stores/appearance-store.svelte";
  import { convertToDisplayUnits, getUnitLabel } from "$lib/utils/formatting";

  // Build date range input from URL parameters
  const dateRangeInput = $derived(useDateRange());

  // Query for reports data
  const reportsQuery = $derived(getReportsData(dateRangeInput));
  const data = $derived(await reportsQuery);

  // Day of week colors - matches Nightscout
  const DAY_COLORS = [
    { name: "Sunday", color: "#808080", shortName: "Sun" }, // Gray
    { name: "Monday", color: "#1e90ff", shortName: "Mon" }, // Blue
    { name: "Tuesday", color: "#009e73", shortName: "Tue" }, // Teal
    { name: "Wednesday", color: "#ff9a00", shortName: "Wed" }, // Orange
    { name: "Thursday", color: "#f0e442", shortName: "Thu" }, // Yellow
    { name: "Friday", color: "#ec7892", shortName: "Fri" }, // Pink
    { name: "Saturday", color: "#d55e00", shortName: "Sat" }, // Red-Orange
  ] as const;

  // Chart scale options
  type ScaleType = "Linear" | "Logarithmic";
  const scaleOptions: ScaleType[] = ["Linear", "Logarithmic"];
  let selectedScale = $state<ScaleType>("Linear");

  // Chart size options
  type SizeOption = { label: string; width: number; height: number };
  const sizeOptions: SizeOption[] = [
    { label: "Small (400 x 160)", width: 400, height: 160 },
    { label: "Medium (600 x 240)", width: 600, height: 240 },
    { label: "Large (800 x 320)", width: 800, height: 320 },
    { label: "Wide (1000 x 300)", width: 1000, height: 300 },
  ];
  let selectedSizeIndex = $state<string>("2");
  const selectedSize = $derived(
    sizeOptions[parseInt(selectedSizeIndex)] ?? sizeOptions[2]
  );

  // Week navigation
  // Initialize from URL param immediately to prevent layout shift
  let weekOffset = $state(
    (() => {
      const weekParam = page.url.searchParams.get("week");
      return weekParam ? parseInt(weekParam) : 0;
    })()
  );

  // Point detail dialog state
  let dialogOpen = $state(false);
  let dialogLoading = $state(false);
  let dialogData = $state<PointInTimeData | null>(null);
  let selectedDayColor = $state("#808080");

  // Calculate the current week's date range based on offset
  const currentWeekRange = $derived.by(() => {
    const now = new Date();
    // Get start of current week (Sunday)
    const startOfWeek = new Date(now);
    const dayOfWeek = now.getDay();
    startOfWeek.setDate(now.getDate() - dayOfWeek + weekOffset * 7);
    startOfWeek.setHours(0, 0, 0, 0);

    // End of week (Saturday)
    const endOfWeek = new Date(startOfWeek);
    endOfWeek.setDate(startOfWeek.getDate() + 6);
    endOfWeek.setHours(23, 59, 59, 999);

    return { start: startOfWeek, end: endOfWeek };
  });

  // Format week range for display
  const weekRangeDisplay = $derived.by(() => {
    const { start, end } = currentWeekRange;
    const options: Intl.DateTimeFormatOptions = {
      month: "short",
      day: "numeric",
      year: "numeric",
    };
    return `${start.toLocaleDateString(undefined, options)} – ${end.toLocaleDateString(undefined, options)}`;
  });

  // Group entries by day of week with time-of-day normalization
  const entriesByDayOfWeek = $derived.by(() => {
    const { start, end } = currentWeekRange;

    // Filter entries to current week
    const weekEntries = (data?.entries ?? []).filter((entry: Entry) => {
      const entryTime =
        entry.mills ?? new Date(entry.dateString ?? "").getTime();
      return entryTime >= start.getTime() && entryTime <= end.getTime();
    });

    // Group by day of week and normalize time to be within a single 24-hour period
    const grouped: Map<
      number,
      {
        original: Entry;
        normalized: Date;
        dayOfWeek: number;
        originalTimestamp: number;
      }[]
    > = new Map();

    for (let i = 0; i < 7; i++) {
      grouped.set(i, []);
    }

    for (const entry of weekEntries) {
      const entryDate = new Date(
        entry.mills ?? new Date(entry.dateString ?? "").getTime()
      );
      const dayOfWeek = entryDate.getDay();

      // Normalize to a single reference day (Jan 1, 2000) keeping only the time
      const normalizedTime = new Date(2000, 0, 1);
      normalizedTime.setHours(
        entryDate.getHours(),
        entryDate.getMinutes(),
        entryDate.getSeconds(),
        entryDate.getMilliseconds()
      );

      grouped.get(dayOfWeek)?.push({
        original: entry,
        normalized: normalizedTime,
        originalTimestamp: entry.mills ?? entryDate.getTime(),
        dayOfWeek,
      });
    }

    return grouped;
  });

  // Create chart data series for each day
  type ChartPoint = { x: Date; y: number; originalTimestamp: number };
  const chartDataSeries = $derived.by(() => {
    const series: {
      dayOfWeek: number;
      color: string;
      name: string;
      data: ChartPoint[];
    }[] = [];

    for (const [dayOfWeek, entries] of entriesByDayOfWeek) {
      if (entries.length === 0) continue;

      // Sort by normalized time
      const sortedEntries = entries.sort(
        (a, b) => a.normalized.getTime() - b.normalized.getTime()
      );

      series.push({
        dayOfWeek,
        color: DAY_COLORS[dayOfWeek].color,
        name: DAY_COLORS[dayOfWeek].name,
        data: sortedEntries.map((e) => ({
          x: e.normalized,
          y: convertToDisplayUnits(
            e.original.sgv ?? e.original.mgdl ?? 0,
            units
          ),
          originalTimestamp: e.originalTimestamp,
        })),
      });
    }

    // Sort by day of week for consistent legend order
    return series.sort((a, b) => a.dayOfWeek - b.dayOfWeek);
  });

  // X-axis domain (24-hour period)
  const xDomain: [Date, Date] = $derived([
    new Date(2000, 0, 1, 0, 0, 0),
    new Date(2000, 0, 1, 23, 59, 59),
  ]);

  // Y-axis domain based on data (now unit-aware)
  const units = $derived(glucoseUnits.current);
  const isMMOL = $derived(units === "mmol");
  const unitLabel = $derived(getUnitLabel(units));
  const is24Hour = $derived(timeFormat.current === "24");

  const yDomain: [number, number] = $derived.by(() => {
    // Find min and max across all data
    const lowThreshold = DEFAULT_THRESHOLDS.low ?? 70;
    const highThreshold = DEFAULT_THRESHOLDS.high ?? 180;
    let minY: number = convertToDisplayUnits(lowThreshold, units);
    let maxY: number = convertToDisplayUnits(highThreshold, units);

    for (const series of chartDataSeries) {
      for (const point of series.data) {
        // point.y is already converted to display units in chartDataSeries
        if (point.y < minY) minY = point.y;
        if (point.y > maxY) maxY = point.y;
      }
    }

    // Add some padding
    const padding = isMMOL ? 1 : 20;
    return [Math.max(0, minY - padding), maxY + padding];
  });

  // Update URL when week changes
  function updateUrl(newOffset: number) {
    const url = new URL(page.url);
    if (newOffset === 0) {
      url.searchParams.delete("week");
    } else {
      url.searchParams.set("week", String(newOffset));
    }
    goto(url.toString(), { replaceState: true, keepFocus: true });
  }

  // Navigation handlers
  function previousWeek() {
    weekOffset--;
    updateUrl(weekOffset);
  }

  function nextWeek() {
    weekOffset++;
    updateUrl(weekOffset);
  }

  function goToCurrentWeek() {
    weekOffset = 0;
    updateUrl(0);
  }

  // Handle point click to show details
  async function handlePointClick(
    point: { x: Date; y: number; originalTimestamp: number },
    dayOfWeek: number
  ) {
    selectedDayColor = DAY_COLORS[dayOfWeek].color;
    dialogOpen = true;
    dialogLoading = true;
    dialogData = null;

    try {
      const result = await getPointInTimeData({
        timestamp: point.originalTimestamp,
      });
      dialogData = result as PointInTimeData | null;
    } catch (error) {
      console.error("Failed to fetch point data:", error);
    } finally {
      dialogLoading = false;
    }
  }

  // Format time for axis labels (respects 24h preference)
  function formatTime(date: Date): string {
    const hours = date.getHours();
    if (is24Hour) {
      return `${hours.toString().padStart(2, "0")}:00`;
    }
    return hours === 0
      ? "12a"
      : hours === 12
        ? "12p"
        : hours > 12
          ? `${hours - 12}p`
          : `${hours}a`;
  }
</script>

<!-- Point Detail Dialog -->
<PointDetailDialog
  bind:open={dialogOpen}
  onOpenChange={(open) => (dialogOpen = open)}
  data={dialogData}
  loading={dialogLoading}
  dayColor={selectedDayColor}
/>

<div class="space-y-6 p-4">
  <!-- Controls -->
  <Card.Root>
    <Card.Content class="p-4">
      <div class="flex flex-wrap items-center justify-between gap-4">
        <!-- Week Navigation -->
        <div class="flex items-center gap-2">
          <Button variant="outline" size="icon" onclick={previousWeek}>
            <ChevronLeft class="h-4 w-4" />
          </Button>
          <div class="flex items-center gap-2 min-w-[200px] justify-center">
            <Calendar class="h-4 w-4 text-muted-foreground" />
            <span class="text-sm font-medium">{weekRangeDisplay}</span>
          </div>
          <Button variant="outline" size="icon" onclick={nextWeek}>
            <ChevronRight class="h-4 w-4" />
          </Button>
          {#if weekOffset !== 0}
            <Button variant="ghost" size="sm" onclick={goToCurrentWeek}>
              Today
            </Button>
          {/if}
        </div>

        <!-- Scale and Size Controls -->
        <div class="flex items-center gap-4">
          <div class="flex items-center gap-2">
            <span class="text-sm text-muted-foreground">Scale:</span>
            <Select.Root type="single" bind:value={selectedScale}>
              <Select.Trigger class="w-[140px]">
                {selectedScale}
              </Select.Trigger>
              <Select.Content>
                {#each scaleOptions as scale}
                  <Select.Item value={scale}>{scale}</Select.Item>
                {/each}
              </Select.Content>
            </Select.Root>
          </div>

          <div class="flex items-center gap-2">
            <span class="text-sm text-muted-foreground">Size:</span>
            <Select.Root type="single" bind:value={selectedSizeIndex}>
              <Select.Trigger class="w-[180px]">
                {selectedSize.label}
              </Select.Trigger>
              <Select.Content>
                {#each sizeOptions as size, index}
                  <Select.Item value={String(index)}>{size.label}</Select.Item>
                {/each}
              </Select.Content>
            </Select.Root>
          </div>
        </div>
      </div>
    </Card.Content>
  </Card.Root>

  <!-- Legend -->
  <Card.Root>
    <Card.Content class="p-4">
      <div class="flex flex-wrap items-center gap-4">
        {#each DAY_COLORS as day, index}
          {@const hasData = chartDataSeries.some((s) => s.dayOfWeek === index)}
          <div class="flex items-center gap-2" class:opacity-30={!hasData}>
            <div
              class="h-3 w-6 rounded"
              style="background-color: {day.color}"
            ></div>
            <span class="text-sm">{day.name}</span>
          </div>
        {/each}
      </div>
    </Card.Content>
  </Card.Root>

  <!-- Chart -->
  <Card.Root>
    <Card.Content class="p-4 flex justify-center">
      <div
        style="width: {selectedSize.width}px; height: {selectedSize.height}px;"
        class="relative"
      >
        {#if chartDataSeries.length > 0}
          <Chart
            data={chartDataSeries[0]?.data ?? []}
            x={(d) => d.x}
            y={(d) => d.y}
            xScale={scaleTime()}
            yScale={selectedScale === "Logarithmic"
              ? scaleLog()
              : scaleLinear()}
            {xDomain}
            {yDomain}
            padding={{ top: 20, right: 30, bottom: 40, left: 50 }}
          >
            <Svg>
              <!-- Axes -->
              <Axis placement="left" rule label={unitLabel} />
              <Axis
                placement="bottom"
                rule
                format={formatTime}
                ticks={[0, 3, 6, 9, 12, 15, 18, 21].map(
                  (h) => new Date(2000, 0, 1, h)
                )}
              />

              <!-- Target range background -->
              {@const targetTop = DEFAULT_THRESHOLDS.targetTop ?? 180}
              {@const targetBottom = DEFAULT_THRESHOLDS.targetBottom ?? 70}
              <Group class="target-range">
                <Rect
                  x={0}
                  y={targetTop}
                  width={100}
                  height={targetTop - targetBottom}
                  class="fill-green-500/20"
                />
              </Group>

              <!-- Render each day's line -->
              {#each chartDataSeries as series (series.dayOfWeek)}
                <Spline
                  data={series.data}
                  x={(d) => d.x}
                  y={(d) => d.y}
                  stroke={series.color}
                  stroke-width={2}
                />
                <!-- Clickable points for this day -->
                <Points
                  data={series.data}
                  x={(d) => d.x}
                  y={(d) => d.y}
                  r={4}
                  fill={series.color}
                  class="cursor-pointer opacity-0 hover:opacity-100 transition-opacity"
                  onclick={(event: MouseEvent) => {
                    // Get data from event target's __data__ property (d3 pattern)
                    const target = event.target as SVGElement & {
                      __data__?: ChartPoint;
                    };
                    const point = target?.__data__;
                    if (point) {
                      handlePointClick(point, series.dayOfWeek);
                    }
                  }}
                />
              {/each}
              <Legend title="Legend" placement="bottom" variant="swatches" />
            </Svg>
          </Chart>
        {:else}
          <div
            class="flex h-full items-center justify-center text-muted-foreground"
          >
            No data available for this week
          </div>
        {/if}
      </div>
    </Card.Content>
  </Card.Root>

  <!-- Summary Stats -->
  <Card.Root>
    <Card.Header>
      <Card.Title>Week Summary</Card.Title>
    </Card.Header>
    <Card.Content>
      <div class="grid grid-cols-2 gap-4 md:grid-cols-4 lg:grid-cols-7">
        {#each DAY_COLORS as day, index}
          {@const dayData = chartDataSeries.find((s) => s.dayOfWeek === index)}
          {@const readings = dayData?.data ?? []}
          {@const avgGlucose =
            readings.length > 0
              ? Math.round(
                  readings.reduce((sum, r) => sum + r.y, 0) / readings.length
                )
              : null}
          <div
            class="rounded-lg border p-3"
            class:opacity-50={readings.length === 0}
          >
            <div class="flex items-center gap-2 mb-2">
              <div
                class="h-3 w-3 rounded-full"
                style="background-color: {day.color}"
              ></div>
              <span class="font-medium text-sm">{day.shortName}</span>
            </div>
            <div class="text-2xl font-bold">
              {avgGlucose ?? "—"}
            </div>
            <div class="text-xs text-muted-foreground">
              {readings.length} readings
            </div>
          </div>
        {/each}
      </div>
    </Card.Content>
  </Card.Root>
</div>
