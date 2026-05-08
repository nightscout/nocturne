<script lang="ts">
  import { goto } from "$app/navigation";
  import { page } from "$app/state";
  import * as Card from "$lib/components/ui/card";
  import { getPunchCardData } from "$api/generated/statistics.generated.remote";
  import { getActiveInstances, getDefinitions, getInstanceHistory } from "$api/generated/trackers.generated.remote";
  import type { TrackerInstanceDto, TrackerDefinitionDto } from "$api";
  import { NotificationUrgency as NotificationUrgencyEnum } from "$api";
  import { Button } from "$lib/components/ui/button";
  import { glucoseUnits } from "$lib/stores/appearance-store.svelte";
  import { getUnitLabel } from "$lib/utils/formatting";
  import CalendarSkeleton from "$lib/components/calendar/CalendarSkeleton.svelte";
  import { TrackerCompletionDialog } from "$lib/components/trackers";
  import CalendarHeader from "$lib/components/calendar/CalendarHeader.svelte";
  import CalendarMonthSummary from "$lib/components/calendar/CalendarMonthSummary.svelte";
  import CalendarDayCell from "$lib/components/calendar/CalendarDayCell.svelte";
  import { coachmark } from "@nocturne/coach";

  // Infer DayStats type from the query result
  type DayStats = NonNullable<
    Awaited<ReturnType<typeof getPunchCardData>>
  >["months"][number]["days"][number];

  // View mode: 'tir' for Time in Range bars, 'profile' for glucose line charts
  type ViewMode = "tir" | "profile";
  let viewMode = $state<ViewMode>(
    (typeof localStorage !== "undefined" &&
      (localStorage.getItem("calendar-view-mode") as ViewMode)) ||
      "tir"
  );

  // Persist view mode preference
  function setViewMode(mode: ViewMode) {
    viewMode = mode;
    if (typeof localStorage !== "undefined") {
      localStorage.setItem("calendar-view-mode", mode);
    }
  }

  // Initialize viewDate from URL params or use current date
  const today = new Date();
  let viewDate = $state(
    (() => {
      const yearParam = page.url.searchParams.get("year");
      const monthParam = page.url.searchParams.get("month");
      if (yearParam && monthParam) {
        const year = parseInt(yearParam);
        const month = parseInt(monthParam) - 1; // URL uses 1-indexed month
        if (!isNaN(year) && !isNaN(month) && month >= 0 && month <= 11) {
          return new Date(year, month, 1);
        }
      }
      return today;
    })()
  );
  const currentMonth = $derived(viewDate.getMonth());
  const currentYear = $derived(viewDate.getFullYear());

  // Calculate date range for current view (full month)
  // Pass ISO strings (not Date objects) so the hydration key is stable across SSR/client timezones
  const dateRangeInput = $derived.by(() => {
    const startDate = `${currentYear}-${String(currentMonth + 1).padStart(2, "0")}-01`;
    const lastDay = new Date(currentYear, currentMonth + 1, 0).getDate();
    const endDate = `${currentYear}-${String(currentMonth + 1).padStart(2, "0")}-${String(lastDay).padStart(2, "0")}`;
    return { startDate, endDate };
  });

  // Query responses
  const punchCardQuery = $derived(getPunchCardData(dateRangeInput));
  const trackersQuery = getActiveInstances();
  const historyQuery = getInstanceHistory({ limit: 100 });
  const definitionsQuery = getDefinitions({});

  // Tracker event types
  type TrackerEventType = "start" | "due" | "completed";
  interface TrackerEvent {
    instance: TrackerInstanceDto;
    eventType: TrackerEventType;
    date: string; // YYYY-MM-DD
  }

  // Navigation functions
  function updateUrl(date: Date) {
    const url = new URL(page.url);
    const today = new Date();
    const isCurrentMonth =
      today.getMonth() === date.getMonth() &&
      today.getFullYear() === date.getFullYear();

    if (isCurrentMonth) {
      url.searchParams.delete("year");
      url.searchParams.delete("month");
    } else {
      url.searchParams.set("year", String(date.getFullYear()));
      url.searchParams.set("month", String(date.getMonth() + 1)); // 1-indexed
    }
    goto(url.toString(), { invalidateAll: true });
  }

  function previousMonth() {
    viewDate = new Date(currentYear, currentMonth - 1, 1);
    updateUrl(viewDate);
  }

  function nextMonth() {
    viewDate = new Date(currentYear, currentMonth + 1, 1);
    updateUrl(viewDate);
  }

  function goToToday() {
    viewDate = new Date();
    updateUrl(viewDate);
  }

  const isCurrentMonth = $derived.by(() => {
    const today = new Date();
    return (
      today.getMonth() === currentMonth && today.getFullYear() === currentYear
    );
  });

  const units = $derived(glucoseUnits.current);
  const unitLabel = $derived(getUnitLabel(units));

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

  // Reactive loading/error states for query results
  const punchCardLoading = $derived(punchCardQuery.loading);
  const punchCardError = $derived(punchCardQuery.error);
  const activeTrackers = $derived(trackersQuery.current ?? []);
  const historyTrackers = $derived(historyQuery.current ?? []);
  const definitions = $derived(definitionsQuery.current ?? []);
  const trackersLoading = $derived(
    trackersQuery.loading || historyQuery.loading || definitionsQuery.loading
  );
  const trackersError = $derived(
    trackersQuery.error || historyQuery.error || definitionsQuery.error
  );

  const daysData = $derived.by(() => {
    const currentData = punchCardQuery.current;
    const monthData = currentData?.months?.find(
      (m) => m.year === currentYear && m.month === currentMonth
    );
    const daysMap = new Map<string, DayStats>();
    if (monthData) {
      for (const day of monthData?.days || []) {
        daysMap.set(day.date, day);
      }
    }
    return {
      days: daysMap,
      maxCarbs: monthData?.maxCarbs ?? 0,
      maxInsulin: monthData?.maxInsulin ?? 0,
      maxDiff: monthData?.maxCarbInsulinDiff ?? 0,
    };
  });

  const calendarGrid = $derived.by(() => {
    const firstDay = new Date(currentYear, currentMonth, 1);
    const lastDay = new Date(currentYear, currentMonth + 1, 0);
    const daysInMonth = lastDay.getDate();
    const startDayOfWeek = firstDay.getDay();

    const grid: (DayStats | null | { empty: true; dayNumber?: number })[][] =
      [];
    let currentDay = 1;

    for (let week = 0; week < 6; week++) {
      const weekDays: (
        | DayStats
        | null
        | { empty: true; dayNumber?: number }
      )[] = [];
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

  function handleDayClick(day: DayStats) {
    goto(`/reports/day-in-review?date=${day.date}`);
  }

  const monthSummary = $derived.by(() => {
    const currentData = punchCardQuery.current;
    const monthData = currentData?.months?.find(
      (m) => m.year === currentYear && m.month === currentMonth
    );
    const summary = monthData?.summary;
    const days = monthData?.days ?? [];
    const daysWithData = days.filter((d) => d.totalReadings || 0 > 0);
    const totalCarbs = days.reduce((sum, d) => sum + (d.totalCarbs ?? 0), 0);
    const totalInsulin = days.reduce(
      (sum, d) => sum + (d.totalInsulin ?? 0),
      0
    );
    const dayCount = daysWithData.length;

    return {
      totalReadings: summary?.totalReadings ?? 0,
      inRangePercent: summary?.inRangePercent ?? 0,
      avgGlucose: summary?.avgGlucose ?? 0,
      avgDailyCarbs: dayCount > 0 ? totalCarbs / dayCount : 0,
      tdd: dayCount > 0 ? totalInsulin / dayCount : 0,
    };
  });

  function getDefinition(
    instance: TrackerInstanceDto,
    defs: TrackerDefinitionDto[]
  ): TrackerDefinitionDto | undefined {
    return defs.find((d) => d.id === instance.definitionId);
  }

  function formatTrackerAge(hours: number | undefined): string {
    if (hours === undefined || hours === null) return "n/a";
    if (hours < 1) return `${Math.floor(hours * 60)}m`;
    if (hours < 24) return `${Math.floor(hours)}h`;
    const days = Math.floor(hours / 24);
    const h = Math.floor(hours % 24);
    return h > 0 ? `${days}d ${h}h` : `${days}d`;
  }

  function getTrackerLevel(
    instance: TrackerInstanceDto,
    def: TrackerDefinitionDto | undefined
  ): string {
    if (!instance.ageHours || !def?.notificationThresholds) return "none";
    const age = instance.ageHours;
    const thresholds = def.notificationThresholds.sort(
      (a, b) => (b.hours ?? 0) - (a.hours ?? 0)
    );
    for (const threshold of thresholds) {
      if (threshold.hours && age >= threshold.hours) {
        const urgency = threshold.urgency;
        if (urgency === NotificationUrgencyEnum.Urgent) return "urgent";
        if (urgency === NotificationUrgencyEnum.Hazard) return "hazard";
        if (urgency === NotificationUrgencyEnum.Warn) return "warn";
        if (urgency === NotificationUrgencyEnum.Info) return "info";
      }
    }
    return "none";
  }

  function buildTrackerEvents(
    active: TrackerInstanceDto[],
    history: TrackerInstanceDto[]
  ): Map<string, TrackerEvent[]> {
    const events = new Map<string, TrackerEvent[]>();
    function addEvent(dateStr: string, event: TrackerEvent) {
      if (!events.has(dateStr)) events.set(dateStr, []);
      events.get(dateStr)!.push(event);
    }
    function toDateStr(date: Date | undefined): string | null {
      if (!date) return null;
      const d = new Date(date);
      return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;
    }
    for (const instance of active) {
      const startDate = toDateStr(instance.startedAt);
      if (startDate)
        addEvent(startDate, { instance, eventType: "start", date: startDate });
      const dueDate = toDateStr(instance.expectedEndAt);
      if (dueDate)
        addEvent(dueDate, { instance, eventType: "due", date: dueDate });
    }
    for (const instance of history) {
      const completedDate = toDateStr(instance.completedAt);
      if (completedDate)
        addEvent(completedDate, {
          instance,
          eventType: "completed",
          date: completedDate,
        });
    }
    return events;
  }

  function getTrackerIconColor(eventType: string, level: string): string {
    if (eventType === "completed") return "text-muted-foreground";
    if (eventType === "start") return "text-green-500 dark:text-green-400";
    switch (level) {
      case "urgent":
        return "text-red-500 dark:text-red-400";
      case "hazard":
        return "text-orange-500 dark:text-orange-400";
      case "warn":
        return "text-yellow-500 dark:text-yellow-400";
      case "info":
        return "text-blue-500 dark:text-blue-400";
      default:
        return "text-muted-foreground";
    }
  }

  function formatTrackerStartTime(startedAt: Date | undefined): string | null {
    if (!startedAt) return null;
    const date = new Date(startedAt);
    if (Number.isNaN(date.getTime())) return null;
    return date.toLocaleTimeString(undefined, {
      hour: "2-digit",
      minute: "2-digit",
    });
  }

  let isCompletionDialogOpen = $state(false);
  let completingInstance = $state<TrackerInstanceDto | null>(null);
  let completingDefinition = $state<TrackerDefinitionDto | null>(null);
  let completingDefaultDate = $state<string | null>(null);
  let openPopoverId = $state<string | null>(null);

  function openCompletionDialog(
    instance: TrackerInstanceDto,
    def: TrackerDefinitionDto | undefined,
    defaultDate: string
  ) {
    openPopoverId = null;
    completingInstance = instance;
    completingDefinition = def ?? null;
    completingDefaultDate = defaultDate;
    isCompletionDialogOpen = true;
  }

  const trackerEvents = $derived(
    buildTrackerEvents(activeTrackers, historyTrackers)
  );

  function handleCompletionDialogClose() {
    isCompletionDialogOpen = false;
    completingInstance = null;
    completingDefinition = null;
    completingDefaultDate = null;
  }

  function handleCompletionComplete() {
    isCompletionDialogOpen = false;
    completingInstance = null;
    completingDefinition = null;
    completingDefaultDate = null;
    goto(page.url.toString(), { invalidateAll: true });
  }
</script>

{#if punchCardLoading}
  <CalendarSkeleton />
{:else if punchCardError}
  <div class="flex items-center justify-center h-full p-6">
    <Card.Root class="max-w-md border-destructive">
      <Card.Content class="py-8">
        <div class="text-center">
          <p class="font-medium text-destructive">
            Failed to load calendar data
          </p>
          <p class="text-sm text-muted-foreground mt-1">
            {punchCardError instanceof Error ? punchCardError.message : "An error occurred"}
          </p>
          <Button class="mt-4" onclick={() => window.location.reload()}>
            Try Again
          </Button>
        </div>
      </Card.Content>
    </Card.Root>
  </div>
{:else}
  <div class="flex flex-col h-full">
    <div
      {@attach coachmark({
        key: "feature-intro.calendar-views",
        title: "View modes",
        description:
          "Switch between Time in Range and Profile views to see different patterns.",
        completeOn: { event: "click" },
      })}
    >
      <CalendarHeader
        {viewDate}
        bind:viewMode
        {isCurrentMonth}
        {MONTH_NAMES}
        {previousMonth}
        {nextMonth}
        {goToToday}
        {setViewMode}
      />
    </div>

    {#if trackersLoading}
      <div class="flex-1 p-4">
        <Card.Root class="h-full">
          <Card.Content class="p-4 h-full flex items-center justify-center">
            <div class="text-muted-foreground">Loading data...</div>
          </Card.Content>
        </Card.Root>
      </div>
    {:else if trackersError}
      <div class="flex-1 p-4">
        <Card.Root class="h-full">
          <Card.Content class="p-4 h-full flex flex-col">
            <div class="grid grid-cols-7 gap-1 mb-2">
              {#each DAY_NAMES as dayName}
                <div
                  class="text-center text-sm font-medium text-muted-foreground py-2"
                >
                  {dayName}
                </div>
              {/each}
            </div>
            <div class="text-center text-muted-foreground py-8">
              <p>Could not load tracker data</p>
              <p class="text-sm">Calendar view is still available</p>
            </div>
          </Card.Content>
        </Card.Root>
      </div>
    {:else}
      <div class="flex-1 p-4">
        <Card.Root class="h-full">
          <Card.Content class="p-4 h-full flex flex-col">
            <div class="grid grid-cols-7 gap-1 mb-2">
              {#each DAY_NAMES as dayName}
                <div
                  class="text-center text-sm font-medium text-muted-foreground py-2"
                >
                  {dayName}
                </div>
              {/each}
            </div>

            <div
              class="flex-1 grid grid-rows-6 gap-1"
              {@attach coachmark({
                key: "feature-intro.calendar-trackers",
                title: "Tracker events",
                description:
                  "Tracker events appear on your calendar \u2014 colored by urgency.",
              })}
            >
              {#each calendarGrid as week}
                <div class="grid grid-cols-7 gap-1">
                  {#each week as day}
                    <CalendarDayCell
                      {day}
                      {viewMode}
                      {currentYear}
                      {currentMonth}
                      {trackerEvents}
                      {definitions}
                      bind:openPopoverId
                      {units}
                      {unitLabel}
                      {handleDayClick}
                      {getDefinition}
                      {getTrackerLevel}
                      {getTrackerIconColor}
                      {formatTrackerStartTime}
                      {formatTrackerAge}
                      {openCompletionDialog}
                    />
                  {/each}
                </div>
              {/each}
            </div>

            <CalendarMonthSummary {monthSummary} {units} {unitLabel} />
          </Card.Content>
        </Card.Root>
      </div>
    {/if}
  </div>
{/if}

<TrackerCompletionDialog
  bind:open={isCompletionDialogOpen}
  instanceId={completingInstance?.id ?? null}
  instanceName={completingInstance?.definitionName ?? "tracker"}
  category={completingDefinition?.category}
  definitionId={completingInstance?.definitionId}
  completionEventType={completingDefinition?.completionEventType}
  defaultCompletedAt={completingDefaultDate ?? undefined}
  onClose={handleCompletionDialogClose}
  onComplete={handleCompletionComplete}
/>
