<script lang="ts">
  import { goto } from "$app/navigation";
  import { page } from "$app/state";
  import * as Card from "$lib/components/ui/card";
  import Button from "$lib/components/ui/button/button.svelte";
  import DateRangePicker from "$lib/components/ui/date-range-picker.svelte";
  import { ChevronLeft, ChevronRight, ArrowLeft } from "lucide-svelte";
  import GlucoseChartCard from "$lib/components/dashboard/GlucoseChartCard.svelte";
  import { getTimeSpansData } from "./data.remote";

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

  // Calculate number of days in range for display
  const dayCount = $derived(
    Math.max(
      1,
      Math.ceil(
        (toDate.getTime() - fromDate.getTime()) / (24 * 60 * 60 * 1000)
      ) + 1
    )
  );

  // Date range for the chart component
  const dateRange = $derived({
    from: data?.dateRange.from ?? fromDate,
    to: data?.dateRange.to ?? toDate,
  });

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
  <DateRangePicker showDaysPresets={true} defaultDays={7} />

  <!-- Use GlucoseChartCard with date range and all spans enabled by default -->
  <GlucoseChartCard
    entries={data?.entries ?? []}
    {dateRange}
    showPredictions={false}
    initialShowOverrideSpans={true}
    initialShowProfileSpans={true}
    initialShowActivitySpans={true}
  />
</div>
