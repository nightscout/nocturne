<script lang="ts">
  import { goto } from "$app/navigation";
  import type { Entry, Treatment } from "$lib/api";
  import { DEFAULT_THRESHOLDS } from "$lib/constants";
  import * as Select from "$lib/components/ui/select";
  import * as Card from "$lib/components/ui/card";
  import * as Tooltip from "$lib/components/ui/tooltip";
  import { Calendar } from "lucide-svelte";
  import type { DayStats } from "$lib/data/month-to-month.remote";

  let { data } = $props();

  // Size mode options
  type SizeMode = "difference" | "carbs" | "insulin";
  const sizeModeOptions: { value: SizeMode; label: string }[] = [
    { value: "difference", label: "Carb/Insulin Balance" },
    { value: "carbs", label: "Carb Intake" },
    { value: "insulin", label: "Insulin Delivery" },
  ];
  let selectedSizeMode = $state<SizeMode>("difference");

  // Colors for glucose distribution (matching Nightscout)
  const GLUCOSE_COLORS = {
    low: "#c30909", // Red
    inRange: "#5ab85a", // Green
    high: "#e9e91a", // Yellow
  };

  // Day of week names
  const DAY_NAMES = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
  const MONTH_NAMES = [
    "January",
    "February",
    "March",
    "April",
    "May",
    "June",
    "July",
    "August",
    "September",
    "October",
    "November",
    "December",
  ];

  // Pie chart size range (min and max radius)
  const MIN_RADIUS = 12;
  const MAX_RADIUS = 28;

  // Calculate months data from entries and treatments
  const monthsData = $derived.by(() => {
    const entries = data.entries as Entry[];
    const treatments = data.treatments as Treatment[];

    // Group by month
    const monthsMap = new Map<
      string,
      {
        year: number;
        month: number;
        monthName: string;
        days: DayStats[];
        maxCarbs: number;
        maxInsulin: number;
        maxDiff: number;
      }
    >();

    // Get date range from data
    const allTimes = entries
      .map((e) => e.mills ?? new Date(e.dateString ?? "").getTime())
      .filter((t) => t > 0);

    if (allTimes.length === 0) {
      return [];
    }

    const minTime = Math.min(...allTimes);
    const maxTime = Math.max(...allTimes);

    // Iterate through each day
    const currentDate = new Date(minTime);
    currentDate.setHours(0, 0, 0, 0);
    const endDate = new Date(maxTime);
    endDate.setHours(23, 59, 59, 999);

    while (currentDate <= endDate) {
      const year = currentDate.getFullYear();
      const month = currentDate.getMonth();
      const monthKey = `${year}-${month}`;

      if (!monthsMap.has(monthKey)) {
        monthsMap.set(monthKey, {
          year,
          month,
          monthName: MONTH_NAMES[month],
          days: [],
          maxCarbs: 0,
          maxInsulin: 0,
          maxDiff: 0,
        });
      }

      // Calculate day stats
      const dayStart = new Date(currentDate);
      const dayEnd = new Date(currentDate);
      dayEnd.setHours(23, 59, 59, 999);

      const dayEntries = entries.filter((e) => {
        const entryTime = e.mills ?? new Date(e.dateString ?? "").getTime();
        return entryTime >= dayStart.getTime() && entryTime <= dayEnd.getTime();
      });

      const dayTreatments = treatments.filter((t) => {
        const treatmentTime = t.mills ?? new Date(t.createdAt ?? "").getTime();
        return (
          treatmentTime >= dayStart.getTime() &&
          treatmentTime <= dayEnd.getTime()
        );
      });

      // Calculate glucose distribution
      const readings = dayEntries
        .filter((e) => e.sgv || e.mgdl)
        .map((e) => e.sgv ?? e.mgdl ?? 0);

      const totalReadings = readings.length;
      const lowThreshold = DEFAULT_THRESHOLDS.low ?? 70;
      const highThreshold = DEFAULT_THRESHOLDS.high ?? 180;

      const lowCount = readings.filter((r) => r < lowThreshold).length;
      const highCount = readings.filter((r) => r >= highThreshold).length;
      const inRangeCount = totalReadings - lowCount - highCount;

      // Calculate treatment totals
      let totalCarbs = 0;
      let totalInsulin = 0;

      for (const treatment of dayTreatments) {
        if (treatment.carbs) {
          totalCarbs += treatment.carbs;
        }
        if (treatment.insulin) {
          totalInsulin += treatment.insulin;
        }
        if (
          treatment.eventType === "Temp Basal" &&
          treatment.rate !== undefined &&
          treatment.duration
        ) {
          totalInsulin += (treatment.rate * treatment.duration) / 60;
        }
      }

      const dayStats: DayStats = {
        date: currentDate.toISOString().split("T")[0],
        timestamp: dayStart.getTime(),
        totalReadings,
        inRangeCount,
        lowCount,
        highCount,
        inRangePercent:
          totalReadings > 0 ? (inRangeCount / totalReadings) * 100 : 0,
        lowPercent: totalReadings > 0 ? (lowCount / totalReadings) * 100 : 0,
        highPercent: totalReadings > 0 ? (highCount / totalReadings) * 100 : 0,
        averageGlucose:
          totalReadings > 0
            ? readings.reduce((sum, r) => sum + r, 0) / totalReadings
            : 0,
        totalCarbs,
        totalInsulin,
        totalBolus: 0,
        totalBasal: 0,
        carbToInsulinRatio: 0,
      };

      // Calculate carb to insulin ratio differential
      const standardCarbRatio = 10;
      const expectedInsulin = totalCarbs / standardCarbRatio;
      dayStats.carbToInsulinRatio =
        totalInsulin > 0 || totalCarbs > 0
          ? Math.abs(expectedInsulin - totalInsulin)
          : 0;

      const monthData = monthsMap.get(monthKey)!;
      monthData.days.push(dayStats);
      monthData.maxCarbs = Math.max(monthData.maxCarbs, totalCarbs);
      monthData.maxInsulin = Math.max(monthData.maxInsulin, totalInsulin);
      monthData.maxDiff = Math.max(
        monthData.maxDiff,
        dayStats.carbToInsulinRatio
      );

      currentDate.setDate(currentDate.getDate() + 1);
    }

    // Convert to array and sort
    return Array.from(monthsMap.values()).sort((a, b) => {
      if (a.year !== b.year) return a.year - b.year;
      return a.month - b.month;
    });
  });

  // Global maximums for normalization
  const globalMaximums = $derived.by(() => {
    let maxCarbs = 0;
    let maxInsulin = 0;
    let maxDiff = 0;

    for (const month of monthsData) {
      maxCarbs = Math.max(maxCarbs, month.maxCarbs);
      maxInsulin = Math.max(maxInsulin, month.maxInsulin);
      maxDiff = Math.max(maxDiff, month.maxDiff);
    }

    return { maxCarbs, maxInsulin, maxDiff };
  });

  // Calculate pie chart radius based on size mode
  function getRadius(day: DayStats): number {
    if (day.totalReadings === 0) return MIN_RADIUS;

    let value = 0;
    let max = 1;

    switch (selectedSizeMode) {
      case "carbs":
        value = day.totalCarbs;
        max = globalMaximums.maxCarbs || 1;
        break;
      case "insulin":
        value = day.totalInsulin;
        max = globalMaximums.maxInsulin || 1;
        break;
      case "difference":
        value = day.carbToInsulinRatio;
        max = globalMaximums.maxDiff || 1;
        break;
    }

    const normalized = Math.min(value / max, 1);
    return MIN_RADIUS + (MAX_RADIUS - MIN_RADIUS) * Math.sqrt(normalized);
  }

  // Create calendar grid for a month
  function getCalendarGrid(year: number, month: number, days: DayStats[]) {
    const firstDay = new Date(year, month, 1);
    const lastDay = new Date(year, month + 1, 0);
    const daysInMonth = lastDay.getDate();
    const startDayOfWeek = firstDay.getDay();

    // Create day lookup
    const dayLookup = new Map(days.map((d) => [d.date, d]));

    // Create grid (6 weeks max)
    const grid: (DayStats | null)[][] = [];
    let currentDay = 1;

    for (let week = 0; week < 6; week++) {
      const weekDays: (DayStats | null)[] = [];

      for (let dayOfWeek = 0; dayOfWeek < 7; dayOfWeek++) {
        if (week === 0 && dayOfWeek < startDayOfWeek) {
          weekDays.push(null);
        } else if (currentDay > daysInMonth) {
          weekDays.push(null);
        } else {
          const dateStr = `${year}-${String(month + 1).padStart(2, "0")}-${String(currentDay).padStart(2, "0")}`;
          weekDays.push(dayLookup.get(dateStr) ?? null);
          currentDay++;
        }
      }

      grid.push(weekDays);

      if (currentDay > daysInMonth) break;
    }

    return grid;
  }

  // Handle day click to navigate to Day in Review
  function handleDayClick(day: DayStats) {
    goto(`/reports/day-in-review?date=${day.date}`);
  }
</script>

<div class="space-y-6 p-4">
  <!-- Header Controls -->
  <Card.Root>
    <Card.Content class="p-4 flex flex-col gap-4">
      <div class="flex flex-wrap items-center justify-between gap-4">
        <div class="flex items-center gap-2">
          <Calendar class="h-5 w-5 text-muted-foreground" />
          <h2 class="text-lg font-semibold">Month to Month Punch Card</h2>
        </div>

        <!-- Size Mode Selector -->
        <div class="flex items-center gap-2">
          <span class="text-sm text-muted-foreground">Pie Size By:</span>
          <Select.Root type="single" bind:value={selectedSizeMode}>
            <Select.Trigger class="w-[180px]">
              {sizeModeOptions.find((o) => o.value === selectedSizeMode)?.label}
            </Select.Trigger>
            <Select.Content>
              {#each sizeModeOptions as option}
                <Select.Item value={option.value}>{option.label}</Select.Item>
              {/each}
            </Select.Content>
          </Select.Root>
        </div>
      </div>
      <div class="flex flex-wrap items-center justify-between gap-4">
        <div class="flex flex-wrap items-center gap-4">
          <div class="flex items-center gap-2">
            <div
              class="h-4 w-4 rounded-full"
              style="background-color: {GLUCOSE_COLORS.inRange}"
            ></div>
            <span class="text-sm">In Range</span>
          </div>
          <div class="flex items-center gap-2">
            <div
              class="h-4 w-4 rounded-full"
              style="background-color: {GLUCOSE_COLORS.low}"
            ></div>
            <span class="text-sm">Low</span>
          </div>
          <div class="flex items-center gap-2">
            <div
              class="h-4 w-4 rounded-full"
              style="background-color: {GLUCOSE_COLORS.high}"
            ></div>
            <span class="text-sm">High</span>
          </div>
        </div>
        <div class="text-sm text-muted-foreground">
          Click on any day to view detailed report
        </div>
      </div>
    </Card.Content>
  </Card.Root>

  <!-- Month Grids -->
  {#each monthsData as month}
    <Card.Root>
      <Card.Header class="pb-2">
        <Card.Title class="text-xl">
          {month.monthName}
          {month.year}
        </Card.Title>
      </Card.Header>
      <Card.Content>
        <!-- Day of week headers -->
        <div class="grid grid-cols-7 gap-2 mb-2">
          {#each DAY_NAMES as dayName}
            <div class="text-center text-sm font-medium text-muted-foreground">
              {dayName}
            </div>
          {/each}
        </div>

        <!-- Calendar grid -->
        {@const grid = getCalendarGrid(month.year, month.month, month.days)}
        <div class="space-y-2">
          {#each grid as week}
            <div class="grid grid-cols-7 gap-2">
              {#each week as day}
                <div
                  class="flex items-center justify-center h-16 rounded-lg border border-border/50 bg-background/50"
                >
                  {#if day && day.totalReadings > 0}
                    {@const radius = getRadius(day)}
                    {@const pieData = [
                      {
                        name: "In Range",
                        value: day.inRangePercent,
                        color: GLUCOSE_COLORS.inRange,
                      },
                      {
                        name: "Low",
                        value: day.lowPercent,
                        color: GLUCOSE_COLORS.low,
                      },
                      {
                        name: "High",
                        value: day.highPercent,
                        color: GLUCOSE_COLORS.high,
                      },
                    ].filter((d) => d.value > 0)}
                    <Tooltip.Root>
                      <Tooltip.Trigger>
                        {#snippet child({ props })}
                          <button
                            {...props}
                            class="relative cursor-pointer hover:scale-110 transition-transform focus:outline-none focus:ring-2 focus:ring-primary rounded-full"
                            onclick={() => handleDayClick(day)}
                          >
                            <svg
                              width={radius * 2 + 4}
                              height={radius * 2 + 4}
                              viewBox="-{radius + 2} -{radius + 2} {radius * 2 +
                                4} {radius * 2 + 4}"
                            >
                              {#each pieData as slice, i}
                                {@const total = pieData.reduce(
                                  (sum, d) => sum + d.value,
                                  0
                                )}
                                {@const startAngle =
                                  pieData
                                    .slice(0, i)
                                    .reduce(
                                      (sum, d) => sum + (d.value / total) * 360,
                                      0
                                    ) - 90}
                                {@const endAngle =
                                  startAngle + (slice.value / total) * 360}
                                {@const startRad = (startAngle * Math.PI) / 180}
                                {@const endRad = (endAngle * Math.PI) / 180}
                                {@const largeArc =
                                  endAngle - startAngle > 180 ? 1 : 0}
                                {@const x1 = radius * Math.cos(startRad)}
                                {@const y1 = radius * Math.sin(startRad)}
                                {@const x2 = radius * Math.cos(endRad)}
                                {@const y2 = radius * Math.sin(endRad)}
                                <path
                                  d="M 0 0 L {x1} {y1} A {radius} {radius} 0 {largeArc} 1 {x2} {y2} Z"
                                  fill={slice.color}
                                />
                              {/each}
                            </svg>
                            <span
                              class="absolute -bottom-1 left-1/2 -translate-x-1/2 text-xs text-muted-foreground font-medium"
                            >
                              {new Date(day.date).getDate()}
                            </span>
                          </button>
                        {/snippet}
                      </Tooltip.Trigger>
                      <Tooltip.Content
                        side="top"
                        class="bg-card text-card-foreground border shadow-lg p-3"
                      >
                        <div class="space-y-1.5">
                          <div class="font-medium text-sm">
                            {new Date(day.date).toLocaleDateString(undefined, {
                              weekday: "long",
                              month: "short",
                              day: "numeric",
                            })}
                          </div>
                          <div class="grid grid-cols-2 gap-x-4 gap-y-1 text-xs">
                            <div class="flex items-center gap-1.5">
                              <span
                                class="w-2 h-2 rounded-full"
                                style="background-color: {GLUCOSE_COLORS.inRange}"
                              ></span>
                              <span class="text-muted-foreground">
                                In Range:
                              </span>
                            </div>
                            <span class="font-medium">
                              {day.inRangePercent.toFixed(1)}%
                            </span>
                            <div class="flex items-center gap-1.5">
                              <span
                                class="w-2 h-2 rounded-full"
                                style="background-color: {GLUCOSE_COLORS.low}"
                              ></span>
                              <span class="text-muted-foreground">Low:</span>
                            </div>
                            <span class="font-medium">
                              {day.lowPercent.toFixed(1)}%
                            </span>
                            <div class="flex items-center gap-1.5">
                              <span
                                class="w-2 h-2 rounded-full"
                                style="background-color: {GLUCOSE_COLORS.high}"
                              ></span>
                              <span class="text-muted-foreground">High:</span>
                            </div>
                            <span class="font-medium">
                              {day.highPercent.toFixed(1)}%
                            </span>
                          </div>
                          <div
                            class="border-t pt-1.5 mt-1.5 grid grid-cols-2 gap-x-4 gap-y-1 text-xs"
                          >
                            <span class="text-muted-foreground">Carbs:</span>
                            <span class="font-medium">
                              {day.totalCarbs.toFixed(0)}g
                            </span>
                            <span class="text-muted-foreground">Insulin:</span>
                            <span class="font-medium">
                              {day.totalInsulin.toFixed(1)}U
                            </span>
                            <span class="text-muted-foreground">
                              Avg Glucose:
                            </span>
                            <span class="font-medium">
                              {day.averageGlucose.toFixed(0)} mg/dL
                            </span>
                          </div>
                          <div
                            class="text-xs text-muted-foreground italic pt-1"
                          >
                            Click to view full day report
                          </div>
                        </div>
                      </Tooltip.Content>
                    </Tooltip.Root>
                  {:else if day}
                    <!-- Day with no data -->
                    <div
                      class="flex flex-col items-center justify-center opacity-30"
                    >
                      <div
                        class="w-6 h-6 rounded-full border-2 border-dashed border-muted-foreground/30"
                      ></div>
                      <span class="text-xs text-muted-foreground mt-1">
                        {new Date(day.date).getDate()}
                      </span>
                    </div>
                  {:else}
                    <!-- Empty cell (before/after month) -->
                    <div class="w-6 h-6"></div>
                  {/if}
                </div>
              {/each}
            </div>
          {/each}
        </div>

        <!-- Month Summary -->
        {#each [month.days.reduce( (acc, d) => ({ readings: acc.readings + d.totalReadings, inRange: acc.inRange + d.inRangeCount, low: acc.low + d.lowCount, high: acc.high + d.highCount, carbs: acc.carbs + d.totalCarbs, insulin: acc.insulin + d.totalInsulin }), { readings: 0, inRange: 0, low: 0, high: 0, carbs: 0, insulin: 0 } )] as monthTotals}
          <div class="mt-4 pt-4 border-t grid grid-cols-2 md:grid-cols-4 gap-4">
            <div class="text-center">
              <div class="text-2xl font-bold text-green-600">
                {monthTotals.readings > 0
                  ? (
                      (monthTotals.inRange / monthTotals.readings) *
                      100
                    ).toFixed(1)
                  : 0}%
              </div>
              <div class="text-sm text-muted-foreground">Time in Range</div>
            </div>
            <div class="text-center">
              <div class="text-2xl font-bold">
                {monthTotals.readings.toLocaleString()}
              </div>
              <div class="text-sm text-muted-foreground">Total Readings</div>
            </div>
            <div class="text-center">
              <div class="text-2xl font-bold">
                {monthTotals.carbs.toFixed(0)}g
              </div>
              <div class="text-sm text-muted-foreground">Total Carbs</div>
            </div>
            <div class="text-center">
              <div class="text-2xl font-bold">
                {monthTotals.insulin.toFixed(1)}U
              </div>
              <div class="text-sm text-muted-foreground">Total Insulin</div>
            </div>
          </div>
        {/each}
      </Card.Content>
    </Card.Root>
  {/each}

  {#if monthsData.length === 0}
    <Card.Root>
      <Card.Content class="p-8 text-center">
        <p class="text-muted-foreground">
          No data available for the selected date range.
        </p>
      </Card.Content>
    </Card.Root>
  {/if}
</div>
