<script lang="ts">
  import { LineChart } from "layerchart";
  import * as Card from "$lib/components/ui/card";
  import { Button } from "$lib/components/ui/button";
  import { ChevronLeft, ChevronRight, Calendar } from "lucide-svelte";
  import { getReportsData } from "$api/reports.remote";
  import { requireDateParamsContext } from "$lib/hooks/date-params.svelte";
  import { contextResource } from "$lib/hooks/resource-context.svelte";
  import { toDayString } from "$lib/utils/date-range";
  import { bg } from "$lib/utils/formatting";
  import { DAY_KEYS, buildWeekdayBuckets } from "./week-to-week.utils";

  const DAY_LABELS = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

  const DAY_SERIES = DAY_KEYS.map((key, i) => ({
    key,
    label: DAY_LABELS[i],
    color: `var(--weekday-${key})`,
  }));

  // Get shared date params from context (set by reports layout)
  // Default: 7 days (today + last 6 days = 1 full week)
  const reportsParams = requireDateParamsContext(7);

  // Create resource with automatic layout registration
  const reportsResource = contextResource(
    () => getReportsData(reportsParams.dateRangeInput),
    { errorTitle: "Error Loading Week Comparison" }
  );

  const dateRangeDisplay = $derived.by(() => {
    const opts: Intl.DateTimeFormatOptions = {
      month: "short",
      day: "numeric",
      year: "numeric",
    };
    return `${reportsParams.startDate.toLocaleDateString(undefined, opts)} – ${reportsParams.endDate.toLocaleDateString(undefined, opts)}`;
  });

  // Each cell is the mean of every reading in that weekday's 5-minute bucket.
  const chartData = $derived(
    buildWeekdayBuckets(reportsResource.current?.entries ?? [], (mgdl) => bg(mgdl))
  );

  // Navigation helpers
  function previousWeek() {
    const newEnd = new Date(reportsParams.startDate);
    newEnd.setDate(newEnd.getDate() - 1);
    const newStart = new Date(newEnd);
    newStart.setDate(newStart.getDate() - 6);
    reportsParams.setCustomRange(toDayString(newStart), toDayString(newEnd));
  }

  function nextWeek() {
    const newStart = new Date(reportsParams.endDate);
    newStart.setDate(newStart.getDate() + 1);
    const newEnd = new Date(newStart);
    newEnd.setDate(newEnd.getDate() + 6);
    reportsParams.setCustomRange(toDayString(newStart), toDayString(newEnd));
  }

  function goToCurrentWeek() {
    reportsParams.reset();
  }
</script>

{#if reportsResource.current}
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
