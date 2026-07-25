<script lang="ts">
  import { goto } from "$app/navigation";
  import { page } from "$app/state";
  import * as Card from "$lib/components/ui/card";
  import { Button } from "$lib/components/ui/button";
  import { Toggle } from "$lib/components/ui/toggle";
  import DateRangePicker from "$lib/components/ui/date-range-picker.svelte";
  import { ChevronLeft, ChevronRight, ArrowLeft } from "lucide-svelte";
  import { StateSpansTimeline } from "$lib/components/dashboard/state-spans-timeline";
  import { getTimeSpansData } from "./data.remote";
  import { dayCount as countDays, resolveDayRange, startOfDay, toDayString } from "$lib/utils/date-range";

  // Default range: the last 7 local calendar days. Deriving these from
  // `toISOString()` named yesterday for anyone east of UTC.
  const defaults = resolveDayRange({ days: 7 }, 7);

  const fromParam = $derived(
    page.url.searchParams.get("from") ?? defaults.from
  );
  const toParam = $derived(
    page.url.searchParams.get("to") ?? defaults.to
  );

  // Fetch data using remote function with date range
  const dataQuery = $derived(
    getTimeSpansData({ from: fromParam, to: toParam })
  );
  const data = $derived(dataQuery.current);

  // Parse dates for display and navigation, as local days rather than UTC midnight
  const fromDate = $derived(startOfDay(fromParam));
  const toDate = $derived(startOfDay(toParam));

  const dayCount = $derived(countDays(fromParam, toParam));

  // Date range for the chart component
  const dateRange = $derived({
    from: data?.dateRange.from ?? fromDate,
    to: data?.dateRange.to ?? toDate,
  });

  // Toggle states for each category (all enabled by default)
  let showPumpModes = $state(true);
  let showProfiles = $state(true);
  let showTempBasals = $state(true);
  let showOverrides = $state(true);
  let showActivities = $state(true);

  /** Shift the window by whole days, keeping its length. */
  function shiftPeriod(direction: -1 | 1) {
    const anchor = direction === -1 ? fromDate : toDate;
    const newFirst = new Date(anchor);
    newFirst.setDate(newFirst.getDate() + direction * (direction === -1 ? dayCount : 1));
    const newLast = new Date(newFirst);
    newLast.setDate(newLast.getDate() + dayCount - 1);
    goto(
      `/time-spans?from=${toDayString(newFirst)}&to=${toDayString(newLast)}`,
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
          <Button variant="outline" size="icon" onclick={() => shiftPeriod(-1)}>
            <ChevronLeft class="h-4 w-4" />
          </Button>
          <div
            class="flex items-center gap-2 min-w-[280px] justify-center text-center"
          >
            <span class="text-lg font-medium">{dateRangeDisplay}</span>
          </div>
          <Button variant="outline" size="icon" onclick={() => shiftPeriod(1)}>
            <ChevronRight class="h-4 w-4" />
          </Button>
        </div>

        <div class="w-24"></div>
      </div>
    </Card.Content>
  </Card.Root>

  <!-- Date Range Picker -->
  <DateRangePicker showDaysPresets={true} defaultDays={7} />

  <!-- Timeline Card -->
  <Card.Root>
    <Card.Header class="pb-2">
      <Card.Title>State Spans Timeline</Card.Title>
      <Card.Description>
        View pump modes, profiles, temp basals, overrides, and activities over time
      </Card.Description>
    </Card.Header>
    <Card.Content>
      <!-- Category toggles -->
      <div class="flex flex-wrap gap-2 mb-4">
        <Toggle
          variant="outline"
          size="sm"
          bind:pressed={showPumpModes}
          aria-label="Toggle pump modes"
        >
          <span
            class="w-2 h-2 rounded-full mr-2"
            style="background-color: var(--pump-mode-automatic);"
          ></span>
          Pump Modes
        </Toggle>
        <Toggle
          variant="outline"
          size="sm"
          bind:pressed={showProfiles}
          aria-label="Toggle profiles"
        >
          <span
            class="w-2 h-2 rounded-full mr-2"
            style="background-color: var(--chart-1);"
          ></span>
          Profiles
        </Toggle>
        <Toggle
          variant="outline"
          size="sm"
          bind:pressed={showTempBasals}
          aria-label="Toggle basal delivery"
        >
          <span
            class="w-2 h-2 rounded-full mr-2"
            style="background-color: var(--insulin-basal);"
          ></span>
          Basal
        </Toggle>
        <Toggle
          variant="outline"
          size="sm"
          bind:pressed={showOverrides}
          aria-label="Toggle overrides"
        >
          <span
            class="w-2 h-2 rounded-full mr-2"
            style="background-color: var(--chart-2);"
          ></span>
          Overrides
        </Toggle>
        <Toggle
          variant="outline"
          size="sm"
          bind:pressed={showActivities}
          aria-label="Toggle activities"
        >
          <span
            class="w-2 h-2 rounded-full mr-2"
            style="background-color: var(--pump-mode-sleep);"
          ></span>
          Activities
        </Toggle>
      </div>

      <!-- Timeline visualization -->
      <StateSpansTimeline
        pumpModeSpans={data?.pumpModeSpans ?? []}
        profileSpans={data?.profileSpans ?? []}
        tempBasalSpans={data?.tempBasalSpans ?? []}
        overrideSpans={data?.overrideSpans ?? []}
        activitySpans={data?.activitySpans ?? []}
        {dateRange}
        {showPumpModes}
        {showProfiles}
        {showTempBasals}
        {showOverrides}
        {showActivities}
      />
    </Card.Content>
  </Card.Root>
</div>
