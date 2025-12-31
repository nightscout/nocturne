<script lang="ts">
  import { LineChart } from "layerchart";
  import { goto } from "$app/navigation";
  import { page } from "$app/state";
  import type { Entry } from "$lib/api";
  import * as Card from "$lib/components/ui/card";
  import Button from "$lib/components/ui/button/button.svelte";
  import { ChevronLeft, ChevronRight, Calendar } from "lucide-svelte";
  import { getReportsData } from "$lib/data/reports.remote";
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

  // Week navigation from URL
  let weekOffset = $state(
    (() => {
      const weekParam = page.url.searchParams.get("week");
      return weekParam ? parseInt(weekParam) : 0;
    })()
  );

  // Calculate week date range
  const currentWeekRange = $derived.by(() => {
    const now = new Date();
    const startOfWeek = new Date(now);
    startOfWeek.setDate(now.getDate() - now.getDay() + weekOffset * 7);
    startOfWeek.setHours(0, 0, 0, 0);

    const endOfWeek = new Date(startOfWeek);
    endOfWeek.setDate(startOfWeek.getDate() + 6);
    endOfWeek.setHours(23, 59, 59, 999);

    return { start: startOfWeek, end: endOfWeek };
  });

  const dateRangeInput = $derived({
    from: currentWeekRange.start.toISOString(),
    to: currentWeekRange.end.toISOString(),
  });

  const reportsQuery = $derived(getReportsData(dateRangeInput));

  const weekRangeDisplay = $derived.by(() => {
    const { start, end } = currentWeekRange;
    const opts: Intl.DateTimeFormatOptions = {
      month: "short",
      day: "numeric",
      year: "numeric",
    };
    return `${start.toLocaleDateString(undefined, opts)} – ${end.toLocaleDateString(undefined, opts)}`;
  });

  // Transform entries into chart data: each row = { time, sun?, mon?, tue?, ... }
  const chartData = $derived.by(() => {
    const entries = reportsQuery.current?.entries ?? [];
    const { start, end } = currentWeekRange;

    // Filter to current week and group by normalized time
    const timeMap = new Map<number, Record<string, number | Date>>();

    for (const entry of entries) {
      const mills = entry.mills ?? new Date(entry.dateString ?? "").getTime();
      if (mills < start.getTime() || mills > end.getTime()) continue;

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
      row[dayKey] = bg(entry.sgv ?? 0);
    }

    // Sort by time
    return Array.from(timeMap.values()).sort(
      (a, b) => (a.time as Date).getTime() - (b.time as Date).getTime()
    );
  });

  // Navigation
  function updateUrl(newOffset: number) {
    const url = new URL(page.url);
    if (newOffset === 0) {
      url.searchParams.delete("week");
    } else {
      url.searchParams.set("week", String(newOffset));
    }
    goto(url.toString(), { replaceState: true, keepFocus: true });
  }

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
</script>

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
