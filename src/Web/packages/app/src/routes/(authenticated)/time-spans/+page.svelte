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
  import {
    dayCount as countDays,
    isDayString,
    resolveDayRange,
    startOfDay,
    toDayString,
  } from "$lib/utils/date-range";
  import { useSearchParams } from "runed/kit";
  import { z } from "zod";

  // Default range: the last 7 local calendar days. Deriving these from
  // `toISOString()` named yesterday for anyone east of UTC.
  const defaults = resolveDayRange({ days: 7 }, 7);

  const fromParam = $derived.by(() => {
    const fromUrl = page.url.searchParams.get("from");
    return isDayString(fromUrl) ? fromUrl : defaults.from;
  });
  const toParam = $derived.by(() => {
    const fromUrl = page.url.searchParams.get("to");
    return isDayString(fromUrl) ? fromUrl : defaults.to;
  });

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

  const CATEGORIES = [
    { key: "pumpModes", label: "Pump Modes", color: "var(--pump-mode-automatic)" },
    { key: "profiles", label: "Profiles", color: "var(--chart-1)" },
    { key: "basal", label: "Basal", color: "var(--insulin-basal)" },
    { key: "overrides", label: "Overrides", color: "var(--chart-2)" },
    { key: "activities", label: "Activities", color: "var(--pump-mode-sleep)" },
  ] as const;

  type CategoryKey = (typeof CATEGORIES)[number]["key"];

  // Every category shows by default; the hidden ones are named in the URL, so a
  // timeline narrowed to one or two categories can be refreshed and shared.
  const viewParams = useSearchParams(
    z.object({ hide: z.string().nullable().default(null) }),
    { showDefaults: false, noScroll: true }
  );

  const hidden = $derived(
    new Set((viewParams.hide ?? "").split(",").filter(Boolean) as CategoryKey[])
  );

  const isShown = (key: CategoryKey) => !hidden.has(key);

  function setShown(key: CategoryKey, shown: boolean) {
    const next = new Set(hidden);
    if (shown) next.delete(key);
    else next.add(key);
    viewParams.hide =
      next.size > 0
        ? CATEGORIES.filter((c) => next.has(c.key))
            .map((c) => c.key)
            .join(",")
        : null;
  }

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
        {#each CATEGORIES as category (category.key)}
          <Toggle
            variant="outline"
            size="sm"
            pressed={isShown(category.key)}
            onPressedChange={(pressed: boolean) => setShown(category.key, pressed)}
            aria-label="Toggle {category.label.toLowerCase()}"
          >
            <span
              class="w-2 h-2 rounded-full mr-2"
              style="background-color: {category.color};"
            ></span>
            {category.label}
          </Toggle>
        {/each}
      </div>

      <!-- Timeline visualization -->
      <StateSpansTimeline
        pumpModeSpans={data?.pumpModeSpans ?? []}
        profileSpans={data?.profileSpans ?? []}
        tempBasalSpans={data?.tempBasalSpans ?? []}
        overrideSpans={data?.overrideSpans ?? []}
        activitySpans={data?.activitySpans ?? []}
        {dateRange}
        showPumpModes={isShown("pumpModes")}
        showProfiles={isShown("profiles")}
        showTempBasals={isShown("basal")}
        showOverrides={isShown("overrides")}
        showActivities={isShown("activities")}
      />
    </Card.Content>
  </Card.Root>
</div>
