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
  import { bg, bgLabel, formatShortDate, hourLabel } from "$lib/utils/formatting";
  import ChartKey from "$lib/components/charts/print/ChartKey.svelte";
  import { dashClass, type TextureKey } from "$lib/components/charts/print/chart-print-patterns";

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

  const WEEKDAY_TEXTURE: Record<Weekday, TextureKey> = {
    Sunday: "weekday-sun",
    Monday: "weekday-mon",
    Tuesday: "weekday-tue",
    Wednesday: "weekday-wed",
    Thursday: "weekday-thu",
    Friday: "weekday-fri",
    Saturday: "weekday-sat",
  };

  const DAY_SERIES = WEEKDAYS.map((key) => {
    const short = key.slice(0, 3).toLowerCase();
    const texture = WEEKDAY_TEXTURE[key];
    return {
      key,
      label: key.slice(0, 3),
      // Darkened in print, where the pale weekday hues (Thursday's yellow) vanish on paper.
      color: `color-mix(in oklab, var(--weekday-${short}) var(--weekday-ink, 100%), black)`,
      texture,
      props: { class: `${dashClass(texture)} print:stroke-2` },
    };
  });

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
  <div class="w-full space-y-2 rounded-sm border p-4 print:[--weekday-ink:45%]">
    {#if chartData.length > 0}
      <div class="h-[280px] @md:h-[360px] print:h-[520px]">
        <LineChart
          data={chartData}
          x="time"
          series={DAY_SERIES}
          padding={{ top: 8, right: 8, bottom: 24, left: 48 }}
          props={{
            xAxis: { format: hourLabel },
            yAxis: { label: bgLabel() },
          }}
        />
      </div>
      <ChartKey
        items={DAY_SERIES.map((s) => ({
          texture: s.texture,
          label: s.label,
          color: s.color,
          shape: "line",
        }))}
      />
    {:else}
      <div
        class="flex h-[280px] items-center justify-center text-muted-foreground @md:h-[360px]"
      >
        No data available for this week
      </div>
    {/if}
  </div>
</div>
{/if}
