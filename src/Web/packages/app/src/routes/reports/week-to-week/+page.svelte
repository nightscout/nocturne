<script lang="ts">
  import { LineChart } from "layerchart";
  import * as Card from "$lib/components/ui/card";
  import Button from "$lib/components/ui/button/button.svelte";
  import { ChevronLeft, ChevronRight, Calendar } from "lucide-svelte";
  import { getReportsData } from "$lib/data/reports.remote";
  import { requireDateParamsContext } from "$lib/hooks/date-params.svelte";
  import { contextResource } from "$lib/hooks/resource-context.svelte";
  import { bg } from "$lib/utils/formatting";

  // Day of week series config
  const DAY_SERIES = [
    { key: "sun", label: "Sun", color: "#808080" },
    { key: "mon", label: "Mon", color: "#1e90ff" },
    { key: "tue", label: "Tue", color: "#009e73" },
    { key: "wed", label: "Wed", color: "#ff9a00" },
    { key: "thu", label: "Thu", color: "#f0e442" },
    { key: "fri", label: "Fri", color: "#ec7892" },
    { key: "sat", label: "Sat", color: "#d55e00" },
  ] as const;

  // Get shared date params from context (set by reports layout)
  // Default: 14 days is ideal for week-to-week comparison (2 full weeks)
  const reportsParams = requireDateParamsContext(14);

  // Create resource with automatic layout registration
  const reportsResource = contextResource(
    () => getReportsData(reportsParams.dateRangeInput),
    { errorTitle: "Error Loading Week Comparison" }
  );

  // Calculate start and end dates from the date params
  const startDate = $derived(new Date(reportsParams.dateRangeInput.from ?? new Date().toISOString()));
  const endDate = $derived(new Date(reportsParams.dateRangeInput.to ?? new Date().toISOString()));

  const dateRangeDisplay = $derived.by(() => {
    const opts: Intl.DateTimeFormatOptions = {
      month: "short",
      day: "numeric",
      year: "numeric",
    };
    return `${startDate.toLocaleDateString(undefined, opts)} – ${endDate.toLocaleDateString(undefined, opts)}`;
  });

  // Transform entries into chart data: each row = { time, sun?, mon?, tue?, ... }
  const chartData = $derived.by(() => {
    const entries = reportsResource.current?.entries ?? [];

    // Group by normalized time across all days in the range
    const timeMap = new Map<number, Record<string, number | Date>>();

    for (const entry of entries) {
      const mills = entry.mills ?? new Date(entry.dateString ?? "").getTime();

      const entryDate = new Date(mills);
      const dayOfWeek = entryDate.getDay();
      const dayKey = DAY_SERIES[dayOfWeek].key;

      // Normalize to time-of-day only (minutes since midnight)
      const minutesInDay = entryDate.getHours() * 60 + entryDate.getMinutes();
      // Round to 5-minute buckets for grouping
      const bucket = Math.round(minutesInDay / 5) * 5;

      if (!timeMap.has(bucket)) {
        // Create a date for x-axis (Jan 1, 2000 + time)
        const time = new Date(2000, 0, 1, Math.floor(bucket / 60), bucket % 60);
        timeMap.set(bucket, { time });
      }

      const row = timeMap.get(bucket)!;
      // Average values if we already have data for this day/time slot
      if (row[dayKey] !== undefined) {
        row[dayKey] = ((row[dayKey] as number) + bg(entry.sgv ?? 0)) / 2;
      } else {
        row[dayKey] = bg(entry.sgv ?? 0);
      }
    }

    // Sort by time
    return Array.from(timeMap.values()).sort(
      (a, b) => (a.time as Date).getTime() - (b.time as Date).getTime()
    );
  });

  // Navigation helpers
  function previousWeek() {
    const newEnd = new Date(startDate);
    newEnd.setDate(newEnd.getDate() - 1);
    const newStart = new Date(newEnd);
    newStart.setDate(newStart.getDate() - 6);
    reportsParams.setCustomRange(
      newStart.toISOString().split("T")[0],
      newEnd.toISOString().split("T")[0]
    );
  }

  function nextWeek() {
    const newStart = new Date(endDate);
    newStart.setDate(newStart.getDate() + 1);
    const newEnd = new Date(newStart);
    newEnd.setDate(newEnd.getDate() + 6);
    reportsParams.setCustomRange(
      newStart.toISOString().split("T")[0],
      newEnd.toISOString().split("T")[0]
    );
  }

  function goToCurrentWeek() {
    reportsParams.reset();
  }
</script>

{#if reportsResource.current}
<div class="space-y-6 p-4">
  <!-- Controls -->
  <Card.Root>
    <Card.Content class="p-4">
      <div class="flex items-center gap-2">
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

  <!-- Chart -->
  <div class="h-[400px] p-4 border rounded-sm">
    {#if chartData.length > 0}
      <LineChart
        data={chartData}
        x="time"
        legend
        series={DAY_SERIES.map((d) => ({
          key: d.key,
          label: d.label,
          color: d.color,
        }))}
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
