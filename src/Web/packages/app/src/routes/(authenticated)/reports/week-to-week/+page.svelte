<script lang="ts">
  import { LineChart } from "layerchart";
  import { parseDate } from "@internationalized/date";
  import * as Card from "$lib/components/ui/card";
  import { Button } from "$lib/components/ui/button";
  import { ChevronLeft, ChevronRight, Calendar } from "lucide-svelte";
  import { getWeekdayAverages } from "$api/reports.remote";
  import type { DayOfWeek } from "$lib/api";
  import { requireDateParamsContext } from "$lib/hooks/date-params.svelte";
  import { contextResource } from "$lib/hooks/resource-context.svelte";
  import { bg, formatShortDate } from "$lib/utils/formatting";

  type Weekday = keyof typeof DayOfWeek;

  /** Series keys are the API's weekday names; the theme's colour tokens use the short form. */
  const WEEKDAYS: Weekday[] = [
    "Sunday",
    "Monday",
    "Tuesday",
    "Wednesday",
    "Thursday",
    "Friday",
    "Saturday",
  ];

  const DAY_SERIES = WEEKDAYS.map((key) => ({
    key,
    label: key.slice(0, 3),
    color: `var(--weekday-${key.slice(0, 3).toLowerCase()})`,
  }));

  // Get shared date params from context (set by reports layout)
  // Default: 7 days (today + last 6 days = 1 full week)
  const reportsParams = requireDateParamsContext(7);

  // Create resource with automatic layout registration
  const weekdayResource = contextResource(
    () => getWeekdayAverages(reportsParams.dateRangeInput),
    { errorTitle: "Error Loading Week Comparison" }
  );

  const dateRangeDisplay = $derived.by(() => {
    return `${formatShortDate(reportsParams.startDate, true)} – ${formatShortDate(reportsParams.endDate, true)}`;
  });

  // One row per populated 5-minute slot, anchored on today's calendar day so the
  // x-axis reads as a time of day; each weekday's mean is shown in the display unit.
  const chartData = $derived.by(() => {
    const today = new Date();
    return (weekdayResource.current ?? []).map((slot) => {
      const row: { time: Date } & Partial<Record<Weekday, number>> = {
        time: new Date(
          today.getFullYear(),
          today.getMonth(),
          today.getDate(),
          0,
          slot.minuteOfDay ?? 0
        ),
      };
      for (const weekday of WEEKDAYS) {
        const mgdl = slot.mean?.[weekday];
        if (mgdl != null) row[weekday] = bg(mgdl);
      }
      return row;
    });
  });

  function previousWeek() {
    const newEnd = parseDate(reportsParams.fromDay).subtract({ days: 1 });
    const newStart = newEnd.subtract({ days: 6 });
    reportsParams.setCustomRange(newStart.toString(), newEnd.toString());
  }

  function nextWeek() {
    const newStart = parseDate(reportsParams.toDay).add({ days: 1 });
    const newEnd = newStart.add({ days: 6 });
    reportsParams.setCustomRange(newStart.toString(), newEnd.toString());
  }

  function goToCurrentWeek() {
    reportsParams.reset();
  }
</script>

{#if weekdayResource.current}
<div class="@container space-y-6 p-3 @md:p-6">
  <!-- Week-stepper controls — navigation chaff; the compared date range stays
       visible in the layout's print header. -->
  <Card.Root class="print:hidden">
    <Card.Content class="p-4">
      <div class="flex flex-wrap items-center justify-center gap-2 @md:justify-start">
        <Button variant="outline" size="icon" onclick={previousWeek}>
          <ChevronLeft class="h-4 w-4" />
        </Button>
        <div class="flex items-center gap-2 min-w-[200px] justify-center">
          <Calendar class="h-4 w-4 text-muted-foreground" />
          <span class="text-sm font-medium">{dateRangeDisplay}</span>
        </div>
        <Button variant="outline" size="icon" onclick={nextWeek}>
          <ChevronRight class="h-4 w-4" />
        </Button>
        {#if !reportsParams.isDefault}
          <Button variant="ghost" size="sm" onclick={goToCurrentWeek}>
            Reset
          </Button>
        {/if}
      </div>
    </Card.Content>
  </Card.Root>

  <!-- Day-of-week comparison chart -->
  <div class="h-[320px] w-full p-4 border rounded-sm @md:h-[400px]">
    {#if chartData.length > 0}
      <LineChart
        data={chartData}
        x="time"
        legend
        series={DAY_SERIES}
      />
    {:else}
      <div
        class="flex h-full items-center justify-center text-muted-foreground"
      >
        No data available for this week
      </div>
    {/if}
  </div>
</div>
{/if}
