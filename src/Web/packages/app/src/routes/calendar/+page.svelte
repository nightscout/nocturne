<script lang="ts">
  import { goto } from "$app/navigation";
  import type { Entry, Treatment } from "$lib/api";
  import { DEFAULT_THRESHOLDS } from "$lib/constants";
  import * as Select from "$lib/components/ui/select";
  import * as Card from "$lib/components/ui/card";
  import * as Tooltip from "$lib/components/ui/tooltip";
  import { Calendar, ChevronLeft, ChevronRight } from "lucide-svelte";
  import { Button } from "$lib/components/ui/button";
  import type { DayStats } from "$lib/data/month-to-month.remote";
  import { getReportsData } from "$lib/data/reports.remote";
  import { cn } from "$lib/utils";

  // Current viewing month/year
  let viewDate = $state(new Date());
  const currentMonth = $derived(viewDate.getMonth());
  const currentYear = $derived(viewDate.getFullYear());

  // Calculate date range for current view (full month)
  const dateRangeInput = $derived.by(() => {
    const firstDay = new Date(currentYear, currentMonth, 1);
    const lastDay = new Date(currentYear, currentMonth + 1, 0);
    return {
      from: firstDay.toISOString().split("T")[0],
      to: lastDay.toISOString().split("T")[0],
    };
  });

  // Query for reports data
  const reportsQuery = $derived(getReportsData(dateRangeInput));
  const data = $derived(await reportsQuery);

  // Navigation functions
  function previousMonth() {
    viewDate = new Date(currentYear, currentMonth - 1, 1);
  }

  function nextMonth() {
    viewDate = new Date(currentYear, currentMonth + 1, 1);
  }

  function goToToday() {
    viewDate = new Date();
  }

  // Check if current view is today's month
  const isCurrentMonth = $derived.by(() => {
    const today = new Date();
    return (
      today.getMonth() === currentMonth && today.getFullYear() === currentYear
    );
  });

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
  const MIN_RADIUS = 14;
  const MAX_RADIUS = 30;

  // Calculate days data from entries and treatments
  const daysData = $derived.by(() => {
    const entries = (data?.entries ?? []) as Entry[];
    const treatments = (data?.treatments ?? []) as Treatment[];

    const daysMap = new Map<string, DayStats>();
    let maxCarbs = 0;
    let maxInsulin = 0;
    let maxDiff = 0;

    // Get first and last day of month
    const firstDay = new Date(currentYear, currentMonth, 1);
    const lastDay = new Date(currentYear, currentMonth + 1, 0);

    // Iterate through each day of the month
    const currentDate = new Date(firstDay);
    while (currentDate <= lastDay) {
      const dayStart = new Date(currentDate);
      dayStart.setHours(0, 0, 0, 0);
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
        if (treatment.carbs && treatment.carbs > 0) {
          totalCarbs += treatment.carbs;
        }
        if (treatment.insulin && treatment.insulin > 0) {
          totalInsulin += treatment.insulin;
        }
      }

      const dateKey = currentDate.toISOString().split("T")[0];
      const dayStats: DayStats = {
        date: dateKey,
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

      daysMap.set(dateKey, dayStats);
      maxCarbs = Math.max(maxCarbs, totalCarbs);
      maxInsulin = Math.max(maxInsulin, totalInsulin);
      maxDiff = Math.max(maxDiff, dayStats.carbToInsulinRatio);

      currentDate.setDate(currentDate.getDate() + 1);
    }

    return { days: daysMap, maxCarbs, maxInsulin, maxDiff };
  });

  // Calculate pie chart radius based on size mode
  function getRadius(day: DayStats): number {
    if (day.totalReadings === 0) return MIN_RADIUS;

    let value = 0;
    let max = 1;

    switch (selectedSizeMode) {
      case "carbs":
        value = day.totalCarbs;
        max = daysData.maxCarbs || 1;
        break;
      case "insulin":
        value = day.totalInsulin;
        max = daysData.maxInsulin || 1;
        break;
      case "difference":
        value = day.carbToInsulinRatio;
        max = daysData.maxDiff || 1;
        break;
    }

    const normalized = Math.min(value / max, 1);
    return MIN_RADIUS + (MAX_RADIUS - MIN_RADIUS) * Math.sqrt(normalized);
  }

  // Create calendar grid for the current month
  const calendarGrid = $derived.by(() => {
    const firstDay = new Date(currentYear, currentMonth, 1);
    const lastDay = new Date(currentYear, currentMonth + 1, 0);
    const daysInMonth = lastDay.getDate();
    const startDayOfWeek = firstDay.getDay();

    // Create grid (6 weeks max)
    const grid: (DayStats | null | { empty: true; dayNumber?: number })[][] =
      [];
    let currentDay = 1;

    for (let week = 0; week < 6; week++) {
      const weekDays: (DayStats | null | { empty: true; dayNumber?: number })[] =
        [];

      for (let dayOfWeek = 0; dayOfWeek < 7; dayOfWeek++) {
        if (week === 0 && dayOfWeek < startDayOfWeek) {
          weekDays.push(null);
        } else if (currentDay > daysInMonth) {
          weekDays.push(null);
        } else {
          const dateStr = `${currentYear}-${String(currentMonth + 1).padStart(2, "0")}-${String(currentDay).padStart(2, "0")}`;
          const dayStats = daysData.days.get(dateStr);
          if (dayStats) {
            weekDays.push(dayStats);
          } else {
            // Day exists but no data
            weekDays.push({ empty: true, dayNumber: currentDay });
          }
          currentDay++;
        }
      }

      grid.push(weekDays);

      if (currentDay > daysInMonth) break;
    }

    return grid;
  });

  // Check if a day is complete (has full day of data)
  function isDayComplete(day: DayStats): boolean {
    // Consider a day "complete" if we have a reasonable amount of readings
    // 288 readings = 1 reading per 5 minutes for 24 hours
    // We'll consider 70% coverage as "complete"
    return day.totalReadings >= 200; // ~70% of 288
  }

  // Check if a day is today
  function isToday(date: string): boolean {
    return date === new Date().toISOString().split("T")[0];
  }

  // Get cell classes based on day state
  function getCellClasses(day: DayStats | null | { empty: true; dayNumber?: number }): string {
    const base = "flex items-center justify-center rounded-lg border min-h-20 relative";
    const isTodayCell = day && "date" in day && isToday(day.date);
    
    return cn(
      base,
      isTodayCell 
        ? "bg-primary/5 border-primary" 
        : "border-border/50 bg-background/50"
    );
  }

  // Handle day click to navigate to Day in Review
  function handleDayClick(day: DayStats) {
    goto(`/reports/day-in-review?date=${day.date}`);
  }

  // Calculate month summary
  const monthSummary = $derived.by(() => {
    const days = Array.from(daysData.days.values());
    return days.reduce(
      (acc, d) => ({
        readings: acc.readings + d.totalReadings,
        inRange: acc.inRange + d.inRangeCount,
        low: acc.low + d.lowCount,
        high: acc.high + d.highCount,
        carbs: acc.carbs + d.totalCarbs,
        insulin: acc.insulin + d.totalInsulin,
      }),
      { readings: 0, inRange: 0, low: 0, high: 0, carbs: 0, insulin: 0 }
    );
  });
</script>

<div class="flex flex-col h-full">
  <!-- Header -->
  <div class="border-b bg-background/95 backdrop-blur supports-backdrop-filter:bg-background/60 sticky top-0 z-10">
    <div class="flex items-center justify-between p-4">
      <div class="flex items-center gap-4">
        <Calendar class="h-6 w-6 text-muted-foreground" />
        <h1 class="text-2xl font-bold">Calendar</h1>
      </div>

      <!-- Navigation Controls -->
      <div class="flex items-center gap-2">
        <Button variant="outline" size="icon" onclick={previousMonth}>
          <ChevronLeft class="h-4 w-4" />
        </Button>
        <div class="text-lg font-semibold min-w-[180px] text-center">
          {MONTH_NAMES[currentMonth]}
          {currentYear}
        </div>
        <Button variant="outline" size="icon" onclick={nextMonth}>
          <ChevronRight class="h-4 w-4" />
        </Button>
        {#if !isCurrentMonth}
          <Button variant="outline" size="sm" onclick={goToToday}>Today</Button>
        {/if}
      </div>

      <!-- Size Mode Selector -->
      <div class="flex items-center gap-2">
        <span class="text-sm text-muted-foreground">Pie Size:</span>
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

    <!-- Legend -->
    <div class="flex items-center justify-between px-4 pb-3">
      <div class="flex items-center gap-4">
        <div class="flex items-center gap-2">
          <div
            class="h-3 w-3 rounded-full"
            style="background-color: {GLUCOSE_COLORS.inRange}"
          ></div>
          <span class="text-sm">In Range</span>
        </div>
        <div class="flex items-center gap-2">
          <div
            class="h-3 w-3 rounded-full"
            style="background-color: {GLUCOSE_COLORS.low}"
          ></div>
          <span class="text-sm">Low</span>
        </div>
        <div class="flex items-center gap-2">
          <div
            class="h-3 w-3 rounded-full"
            style="background-color: {GLUCOSE_COLORS.high}"
          ></div>
          <span class="text-sm">High</span>
        </div>
      </div>
      <div class="text-sm text-muted-foreground">
        Click any day to view detailed report
      </div>
    </div>
  </div>

  <!-- Calendar Grid -->
  <div class="flex-1 p-4">
    <Card.Root class="h-full">
      <Card.Content class="p-4 h-full flex flex-col">
        <!-- Day of week headers -->
        <div class="grid grid-cols-7 gap-1 mb-2">
          {#each DAY_NAMES as dayName}
            <div class="text-center text-sm font-medium text-muted-foreground py-2">
              {dayName}
            </div>
          {/each}
        </div>

        <!-- Calendar grid -->
        <div class="flex-1 grid grid-rows-6 gap-1">
          {#each calendarGrid as week}
            <div class="grid grid-cols-7 gap-1">
              {#each week as day}
                <div class={getCellClasses(day)}>
                  {#if day && "date" in day && day.totalReadings > 0}
                    {@const complete = isDayComplete(day)}
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

                    <!-- Day number in corner -->
                    <span
                      class="absolute top-1 left-2 text-xs text-muted-foreground font-medium"
                    >
                      {new Date(day.date).getDate()}
                    </span>

                    {#if complete}
                      <!-- Show pie chart for complete days -->
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
                                        (sum, d) =>
                                          sum + (d.value / total) * 360,
                                        0
                                      ) - 90}
                                  {@const endAngle =
                                    startAngle + (slice.value / total) * 360}
                                  {@const startRad =
                                    (startAngle * Math.PI) / 180}
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
                            <div
                              class="grid grid-cols-2 gap-x-4 gap-y-1 text-xs"
                            >
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
                    {:else}
                      <!-- Partial day - show small indicator -->
                      <Tooltip.Root>
                        <Tooltip.Trigger>
                          {#snippet child({ props })}
                            <button
                              {...props}
                              class="cursor-pointer hover:scale-110 transition-transform focus:outline-none focus:ring-2 focus:ring-primary rounded-full opacity-60"
                              onclick={() => handleDayClick(day)}
                            >
                              <div
                                class="w-8 h-8 rounded-full border-2 border-dashed flex items-center justify-center text-xs text-muted-foreground"
                                style="border-color: {GLUCOSE_COLORS.inRange}"
                              >
                                {Math.round((day.totalReadings / 288) * 100)}%
                              </div>
                            </button>
                          {/snippet}
                        </Tooltip.Trigger>
                        <Tooltip.Content side="top">
                          <div class="text-sm">
                            <div class="font-medium">Partial Data</div>
                            <div class="text-muted-foreground">
                              {day.totalReadings} of ~288 readings
                            </div>
                          </div>
                        </Tooltip.Content>
                      </Tooltip.Root>
                    {/if}
                  {:else if day && "empty" in day}
                    <!-- Day with no data -->
                    <span class="absolute top-1 left-2 text-xs text-muted-foreground">
                      {day.dayNumber}
                    </span>
                    <div
                      class="w-6 h-6 rounded-full border-2 border-dashed border-muted-foreground/20"
                    ></div>
                  {:else if day && "date" in day}
                    <!-- Day exists in data but has no readings -->
                    <span class="absolute top-1 left-2 text-xs text-muted-foreground">
                      {new Date(day.date).getDate()}
                    </span>
                    <div
                      class="w-6 h-6 rounded-full border-2 border-dashed border-muted-foreground/20"
                    ></div>
                  {:else}
                    <!-- Empty cell (before/after month) -->
                  {/if}
                </div>
              {/each}
            </div>
          {/each}
        </div>

        <!-- Month Summary -->
        {#if monthSummary.readings > 0}
          <div class="mt-4 pt-4 border-t grid grid-cols-2 md:grid-cols-4 gap-4">
            <div class="text-center">
              <div class="text-2xl font-bold text-green-600">
                {monthSummary.readings > 0
                  ? ((monthSummary.inRange / monthSummary.readings) * 100).toFixed(1)
                  : 0}%
              </div>
              <div class="text-sm text-muted-foreground">Time in Range</div>
            </div>
            <div class="text-center">
              <div class="text-2xl font-bold">
                {monthSummary.readings.toLocaleString()}
              </div>
              <div class="text-sm text-muted-foreground">Total Readings</div>
            </div>
            <div class="text-center">
              <div class="text-2xl font-bold">
                {monthSummary.carbs.toFixed(0)}g
              </div>
              <div class="text-sm text-muted-foreground">Total Carbs</div>
            </div>
            <div class="text-center">
              <div class="text-2xl font-bold">
                {monthSummary.insulin.toFixed(1)}U
              </div>
              <div class="text-sm text-muted-foreground">Total Insulin</div>
            </div>
          </div>
        {/if}
      </Card.Content>
    </Card.Root>
  </div>
</div>
