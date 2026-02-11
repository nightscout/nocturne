<script lang="ts">
  import { goto } from "$app/navigation";
  import { page } from "$app/state";
  import * as Card from "$lib/components/ui/card";
  import * as Tooltip from "$lib/components/ui/tooltip";
  import * as Popover from "$lib/components/ui/popover";
  import {
    Calendar,
    ChevronLeft,
    ChevronRight,
    Play,
    CheckCircle,
    CalendarClock,
    Check,
  } from "lucide-svelte";
  import { Button } from "$lib/components/ui/button";
  import { getPunchCardData } from "$lib/data/month-to-month.remote";
  import {
    getActiveInstances,
    getDefinitions,
    getInstanceHistory,
  } from "$lib/data/generated";
  import type { TrackerInstanceDto, TrackerDefinitionDto } from "$api";
  import {
    NotificationUrgency as NotificationUrgencyEnum,
    TrackerCategory,
  } from "$api";
  import { TrackerCategoryIcon } from "$lib/components/icons";
  import { cn } from "$lib/utils";
  import { glucoseUnits } from "$lib/stores/appearance-store.svelte";
  import { formatGlucoseValue, getUnitLabel } from "$lib/utils/formatting";
  import CalendarSkeleton from "$lib/components/calendar/CalendarSkeleton.svelte";
  import DayStackedBar from "$lib/components/calendar/DayStackedBar.svelte";
  import DayGlucoseProfile from "$lib/components/calendar/DayGlucoseProfile.svelte";
  import * as ToggleGroup from "$lib/components/ui/toggle-group";
  import { TrackerCompletionDialog } from "$lib/components/trackers";

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
      return new Date();
    })()
  );
  const currentMonth = $derived(viewDate.getMonth());
  const currentYear = $derived(viewDate.getFullYear());

  // Calculate date range for current view (full month)
  // Use full ISO timestamps to ensure the entire day is included
  const dateRangeInput = $derived.by(() => {
    const firstDay = new Date(currentYear, currentMonth, 1, 0, 0, 0, 0);
    const lastDay = new Date(currentYear, currentMonth + 1, 0, 23, 59, 59, 999);
    return {
      fromDate: firstDay.toISOString().split("T")[0],
      toDate: lastDay.toISOString().split("T")[0],
    };
  });

  // Query for punch card data (calculations done on backend)
  const punchCardQuery = $derived(getPunchCardData(dateRangeInput));

  // Queries for trackers
  const trackersQuery = $derived(getActiveInstances());
  const historyQuery = $derived(getInstanceHistory({ limit: 100 }));
  const definitionsQuery = $derived(getDefinitions({}));

  // Tracker event types for calendar display
  type TrackerEventType = "start" | "due" | "completed";
  interface TrackerEvent {
    instance: TrackerInstanceDto;
    eventType: TrackerEventType;
    date: string; // YYYY-MM-DD
  }

  // Update URL when month changes
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

  // Navigation functions
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

  // Check if current view is today's month
  const isCurrentMonth = $derived.by(() => {
    const today = new Date();
    return (
      today.getMonth() === currentMonth && today.getFullYear() === currentYear
    );
  });

  // Get units preference
  const units = $derived(glucoseUnits.current);
  const unitLabel = $derived(getUnitLabel(units));

  // Colors for glucose distribution (using CSS variables for theme support)

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

  // Get days data from backend response
  const daysData = $derived.by(() => {
    const currentData = punchCardQuery.current;

    // Find the month data that matches the current view
    // The API might return multiple months (e.g. if start date falls in previous month due to timezone)
    // so we need to explicitly find the one we're looking for
    const monthData = currentData?.months?.find(
      (m) => m.year === currentYear && m.month === currentMonth
    );
    const daysMap = new Map<string, DayStats>();

    if (monthData) {
      for (const day of monthData.days) {
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

  // Check if a day is today
  function isToday(date: string): boolean {
    return date === new Date().toISOString().split("T")[0];
  }

  // Get cell classes based on day state
  function getCellClasses(
    day: DayStats | null | { empty: true; dayNumber?: number }
  ): string {
    const base =
      "flex items-center justify-center rounded-lg border min-h-20 relative";
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

  // Calculate month summary from backend data
  const monthSummary = $derived.by(() => {
    const days = Array.from(daysData.days.values());
    const daysWithData = days.filter((d) => d.totalReadings > 0);
    const dayCount = daysWithData.length;

    const totals = days.reduce(
      (acc, d) => ({
        readings: acc.readings + d.totalReadings,
        inRange: acc.inRange + d.inRangeCount,
        low: acc.low + d.lowCount,
        high: acc.high + d.highCount,
        carbs: acc.carbs + d.totalCarbs,
        insulin: acc.insulin + d.totalInsulin,
        glucose: acc.glucose + (d.averageGlucose > 0 ? d.averageGlucose : 0),
        glucoseDays: acc.glucoseDays + (d.averageGlucose > 0 ? 1 : 0),
      }),
      {
        readings: 0,
        inRange: 0,
        low: 0,
        high: 0,
        carbs: 0,
        insulin: 0,
        glucose: 0,
        glucoseDays: 0,
      }
    );

    return {
      ...totals,
      dayCount,
      avgCarbs: dayCount > 0 ? totals.carbs / dayCount : 0,
      avgInsulin: dayCount > 0 ? totals.insulin / dayCount : 0,
      avgGlucose:
        totals.glucoseDays > 0 ? totals.glucose / totals.glucoseDays : 0,
      inRangePercent:
        totals.readings > 0 ? (totals.inRange / totals.readings) * 100 : 0,
      lowPercent:
        totals.readings > 0 ? (totals.low / totals.readings) * 100 : 0,
      highPercent:
        totals.readings > 0 ? (totals.high / totals.readings) * 100 : 0,
    };
  });

  // Tracker helpers
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
  type AlertLevel = "none" | "info" | "warn" | "hazard" | "urgent";

  function getTrackerLevel(
    instance: TrackerInstanceDto,
    def: TrackerDefinitionDto | undefined
  ): AlertLevel {
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

  // Build tracker events from active and history instances
  function buildTrackerEvents(
    active: TrackerInstanceDto[],
    history: TrackerInstanceDto[]
  ): Map<string, TrackerEvent[]> {
    const events = new Map<string, TrackerEvent[]>();

    function addEvent(dateStr: string, event: TrackerEvent) {
      if (!events.has(dateStr)) {
        events.set(dateStr, []);
      }
      events.get(dateStr)!.push(event);
    }

    function toDateStr(date: Date | undefined): string | null {
      if (!date) return null;
      const d = new Date(date);
      return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;
    }

    // Active trackers: show start + due date
    for (const instance of active) {
      const startDate = toDateStr(instance.startedAt);
      if (startDate) {
        addEvent(startDate, { instance, eventType: "start", date: startDate });
      }

      const dueDate = toDateStr(instance.expectedEndAt);
      if (dueDate) {
        addEvent(dueDate, { instance, eventType: "due", date: dueDate });
      }
    }

    // Completed trackers: show completion date only
    for (const instance of history) {
      const completedDate = toDateStr(instance.completedAt);
      if (completedDate) {
        addEvent(completedDate, {
          instance,
          eventType: "completed",
          date: completedDate,
        });
      }
    }

    return events;
  }

  // Get icon color class based on event type and level
  function getTrackerIconColor(
    eventType: TrackerEventType,
    level: AlertLevel
  ): string {
    if (eventType === "completed") {
      return "text-muted-foreground";
    }
    if (eventType === "start") {
      return "text-green-500 dark:text-green-400";
    }
    // Due date - use urgency level
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

  // Completion dialog state
  let isCompletionDialogOpen = $state(false);
  let completingInstance = $state<TrackerInstanceDto | null>(null);
  let completingDefinition = $state<TrackerDefinitionDto | null>(null);
  let completingDefaultDate = $state<string | null>(null);

  // Track which popover is open (by instance ID + date)
  let openPopoverId = $state<string | null>(null);

  function openCompletionDialog(
    instance: TrackerInstanceDto,
    def: TrackerDefinitionDto | undefined,
    defaultDate: string
  ) {
    // Close any open popover first
    openPopoverId = null;
    completingInstance = instance;
    completingDefinition = def ?? null;
    completingDefaultDate = defaultDate;
    isCompletionDialogOpen = true;
  }

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
    // Refresh tracker data by invalidating the page
    goto(page.url.toString(), { invalidateAll: true });
  }
</script>

{#await punchCardQuery}
  <CalendarSkeleton />
{:then}
  <div class="flex flex-col h-full">
    <!-- Header -->
    <div
      class="border-b bg-background/95 backdrop-blur supports-backdrop-filter:bg-background/60 sticky top-0 z-10"
    >
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
            <Button variant="outline" size="sm" onclick={goToToday}>
              Today
            </Button>
          {/if}
        </div>
      </div>

      <!-- Legend and View Toggle -->
      <div class="flex items-center justify-between px-4 pb-3">
        <div class="flex items-center gap-6">
          <!-- View Mode Toggle -->
          <ToggleGroup.Root
            type="single"
            value={viewMode}
            onValueChange={(value) => value && setViewMode(value as ViewMode)}
            class="border rounded-md"
          >
            <ToggleGroup.Item value="tir" class="text-xs px-3">
              TIR
            </ToggleGroup.Item>
            <ToggleGroup.Item value="profile" class="text-xs px-3">
              Profile
            </ToggleGroup.Item>
          </ToggleGroup.Root>

          <!-- Glucose colors -->
          <div class="flex items-center gap-3">
            <div class="flex items-center gap-1.5">
              <div class="h-3 w-3 rounded bg-glucose-in-range"></div>
              <span class="text-sm">In Range</span>
            </div>
            <div class="flex items-center gap-1.5">
              <div class="h-3 w-3 rounded bg-glucose-low"></div>
              <span class="text-sm">Low</span>
            </div>
            <div class="flex items-center gap-1.5">
              <div class="h-3 w-3 rounded bg-glucose-high"></div>
              <span class="text-sm">High</span>
            </div>
          </div>

          <!-- Mode-specific explanation -->
          {#if viewMode === "profile"}
            <div
              class="flex items-center gap-3 text-xs text-muted-foreground border-l pl-4"
            >
              <span>Daily glucose profile</span>
              <span>·</span>
              <span>Green band = target range</span>
            </div>
          {/if}
        </div>
        <div class="text-sm text-muted-foreground">
          Click any day to view detailed report
        </div>
      </div>
    </div>

    <!-- Calendar Grid -->
    {#await Promise.all([trackersQuery, historyQuery, definitionsQuery])}
      <div class="flex-1 p-4">
        <Card.Root class="h-full">
          <Card.Content class="p-4 h-full flex items-center justify-center">
            <div class="text-muted-foreground">Loading data...</div>
          </Card.Content>
        </Card.Root>
      </div>
    {:then [activeTrackers, historyTrackers, definitions]}
      {@const trackerEvents = buildTrackerEvents(
        activeTrackers ?? [],
        historyTrackers ?? []
      )}
      <div class="flex-1 p-4">
        <Card.Root class="h-full">
          <Card.Content class="p-4 h-full flex flex-col">
            <!-- Day of week headers -->
            <div class="grid grid-cols-7 gap-1 mb-2">
              {#each DAY_NAMES as dayName}
                <div
                  class="text-center text-sm font-medium text-muted-foreground py-2"
                >
                  {dayName}
                </div>
              {/each}
            </div>

            <!-- Calendar grid -->
            <div class="flex-1 grid grid-rows-6 gap-1">
              {#each calendarGrid as week}
                <div class="grid grid-cols-7 gap-1">
                  {#each week as day}
                    {@const dateStr =
                      day && "date" in day
                        ? day.date
                        : day && "dayNumber" in day
                          ? `${currentYear}-${String(currentMonth + 1).padStart(2, "0")}-${String(day.dayNumber).padStart(2, "0")}`
                          : null}
                    {@const dayTrackerEvents = dateStr
                      ? (trackerEvents.get(dateStr) ?? [])
                      : []}
                    <div class={getCellClasses(day)}>
                      {#if day && "date" in day && day.totalReadings > 0}
                        <!-- Day number in corner -->
                        <span
                          class="absolute top-1 left-2 text-xs text-muted-foreground font-medium z-10"
                        >
                          {new Date(day.date).getDate()}
                        </span>

                        <!-- Tracker icons in top-right corner -->
                        {#if dayTrackerEvents.length > 0}
                          <div class="absolute top-1 right-1 flex gap-0.5 z-10">
                            {#each dayTrackerEvents as event}
                              {@const def = getDefinition(
                                event.instance,
                                definitions ?? []
                              )}
                              {@const level =
                                event.eventType === "due"
                                  ? getTrackerLevel(event.instance, def)
                                  : "none"}
                              {@const category =
                                def?.category ?? TrackerCategory.Consumable}
                              {@const startTime = formatTrackerStartTime(
                                event.instance.startedAt
                              )}
                              {@const popoverId = `${event.instance.id}-${event.date}`}
                              <Popover.Root
                                open={openPopoverId === popoverId}
                                onOpenChange={(open) =>
                                  (openPopoverId = open ? popoverId : null)}
                              >
                                <Popover.Trigger>
                                  {#snippet child({ props })}
                                    <button
                                      {...props}
                                      class={cn(
                                        "h-4 w-4 rounded-full flex items-center justify-center hover:scale-125 transition-transform",
                                        getTrackerIconColor(
                                          event.eventType,
                                          level
                                        )
                                      )}
                                      title={event.instance.definitionName}
                                    >
                                      <TrackerCategoryIcon
                                        {category}
                                        class="h-3 w-3"
                                      />
                                    </button>
                                  {/snippet}
                                </Popover.Trigger>
                                <Popover.Content class="w-64 p-3" side="top">
                                  <div class="space-y-2">
                                    <div class="flex items-center gap-2">
                                      <TrackerCategoryIcon
                                        {category}
                                        class="h-4 w-4 text-muted-foreground"
                                      />
                                      <span class="font-medium text-sm">
                                        {event.instance.definitionName}
                                      </span>
                                    </div>
                                    <div class="text-xs space-y-1">
                                      {#if event.eventType === "start"}
                                        <div
                                          class="flex items-center gap-1 text-green-600 dark:text-green-400"
                                        >
                                          <Play class="h-3 w-3" />
                                          <span>
                                            {startTime
                                              ? `Started at ${startTime}`
                                              : "Started on this day"}
                                          </span>
                                        </div>
                                        {#if event.instance.startNotes}
                                          <div class="text-muted-foreground">
                                            Note: {event.instance.startNotes}
                                          </div>
                                        {/if}
                                      {:else if event.eventType === "completed"}
                                        <div
                                          class="flex items-center gap-1 text-muted-foreground"
                                        >
                                          <CheckCircle class="h-3 w-3" />
                                          <span>Completed on this day</span>
                                        </div>
                                        {#if event.instance.completionNotes}
                                          <div class="text-muted-foreground">
                                            Note: {event.instance
                                              .completionNotes}
                                          </div>
                                        {/if}
                                      {:else}
                                        <div
                                          class={cn(
                                            "flex items-center gap-1",
                                            level === "urgent"
                                              ? "text-red-600 dark:text-red-400"
                                              : level === "hazard"
                                                ? "text-orange-600 dark:text-orange-400"
                                                : level === "warn"
                                                  ? "text-yellow-600 dark:text-yellow-400"
                                                  : "text-blue-600 dark:text-blue-400"
                                          )}
                                        >
                                          <CalendarClock class="h-3 w-3" />
                                          <span>Due on this day</span>
                                        </div>
                                        <div class="text-muted-foreground">
                                          Age: {formatTrackerAge(
                                            event.instance.ageHours
                                          )}
                                        </div>
                                        <Button
                                          size="sm"
                                          variant="outline"
                                          class="mt-2 w-full h-7 text-xs"
                                          onclick={() =>
                                            openCompletionDialog(
                                              event.instance,
                                              def,
                                              event.date
                                            )}
                                        >
                                          <Check class="h-3 w-3 mr-1" />
                                          Complete
                                        </Button>
                                      {/if}
                                    </div>
                                  </div>
                                </Popover.Content>
                              </Popover.Root>
                            {/each}
                          </div>
                        {/if}

                        <!-- Show chart based on view mode -->
                        <Tooltip.Root>
                          <Tooltip.Trigger>
                            {#snippet child({ props })}
                              {#if viewMode === "tir"}
                                <div
                                  {...props}
                                  class="absolute inset-0 p-2 pt-6"
                                >
                                  <DayStackedBar
                                    lowPercent={day.lowPercent}
                                    inRangePercent={day.inRangePercent}
                                    highPercent={day.highPercent}
                                    onclick={() => handleDayClick(day)}
                                  />
                                </div>
                              {:else}
                                <div {...props} class="absolute inset-0">
                                  <DayGlucoseProfile
                                    entries={day.entries}
                                    onclick={() => handleDayClick(day)}
                                  />
                                </div>
                              {/if}
                            {/snippet}
                          </Tooltip.Trigger>
                          <Tooltip.Content
                            side="top"
                            class="bg-card text-card-foreground border shadow-lg p-3"
                          >
                            <div class="space-y-1.5">
                              <div class="font-medium text-sm">
                                {new Date(day.date).toLocaleDateString(
                                  undefined,
                                  {
                                    weekday: "long",
                                    month: "short",
                                    day: "numeric",
                                  }
                                )}
                              </div>
                              <div
                                class="grid grid-cols-2 gap-x-4 gap-y-1 text-xs"
                              >
                                <div class="flex items-center gap-1.5">
                                  <span
                                    class="w-2 h-2 rounded-full bg-glucose-in-range"
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
                                    class="w-2 h-2 rounded-full bg-glucose-low"
                                  ></span>
                                  <span class="text-muted-foreground">
                                    Low:
                                  </span>
                                </div>
                                <span class="font-medium">
                                  {day.lowPercent.toFixed(1)}%
                                </span>
                                <div class="flex items-center gap-1.5">
                                  <span
                                    class="w-2 h-2 rounded-full bg-glucose-high"
                                  ></span>
                                  <span class="text-muted-foreground">
                                    High:
                                  </span>
                                </div>
                                <span class="font-medium">
                                  {day.highPercent.toFixed(1)}%
                                </span>
                              </div>
                              <div
                                class="border-t pt-1.5 mt-1.5 grid grid-cols-2 gap-x-4 gap-y-1 text-xs"
                              >
                                <span class="text-muted-foreground">
                                  Carbs:
                                </span>
                                <span class="font-medium">
                                  {day.totalCarbs.toFixed(0)}g
                                </span>
                                <span class="text-muted-foreground">
                                  Insulin:
                                </span>
                                <span class="font-medium">
                                  {day.totalInsulin.toFixed(1)}U
                                </span>
                                <span class="text-muted-foreground">
                                  Avg Glucose:
                                </span>
                                <span class="font-medium">
                                  {formatGlucoseValue(
                                    day.averageGlucose,
                                    units
                                  )}
                                  {unitLabel}
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
                      {:else if day && "empty" in day}
                        <!-- Day with no data -->
                        <span
                          class="absolute top-1 left-2 text-xs text-muted-foreground"
                        >
                          {day.dayNumber}
                        </span>
                        <!-- Tracker icons for empty days -->
                        {#if dayTrackerEvents.length > 0}
                          <div class="absolute top-1 right-1 flex gap-0.5">
                            {#each dayTrackerEvents as event}
                              {@const def = getDefinition(
                                event.instance,
                                definitions ?? []
                              )}
                              {@const level =
                                event.eventType === "due"
                                  ? getTrackerLevel(event.instance, def)
                                  : "none"}
                              {@const category =
                                def?.category ?? TrackerCategory.Consumable}
                              {@const startTime = formatTrackerStartTime(
                                event.instance.startedAt
                              )}
                              {@const popoverId = `${event.instance.id}-${event.date}`}
                              <Popover.Root
                                open={openPopoverId === popoverId}
                                onOpenChange={(open) =>
                                  (openPopoverId = open ? popoverId : null)}
                              >
                                <Popover.Trigger>
                                  {#snippet child({ props })}
                                    <button
                                      {...props}
                                      class={cn(
                                        "h-4 w-4 rounded-full flex items-center justify-center hover:scale-125 transition-transform",
                                        getTrackerIconColor(
                                          event.eventType,
                                          level
                                        )
                                      )}
                                      title={event.instance.definitionName}
                                    >
                                      <TrackerCategoryIcon
                                        {category}
                                        class="h-3 w-3"
                                      />
                                    </button>
                                  {/snippet}
                                </Popover.Trigger>
                                <Popover.Content class="w-64 p-3" side="top">
                                  <div class="space-y-2">
                                    <div class="flex items-center gap-2">
                                      <TrackerCategoryIcon
                                        {category}
                                        class="h-4 w-4 text-muted-foreground"
                                      />
                                      <span class="font-medium text-sm">
                                        {event.instance.definitionName}
                                      </span>
                                    </div>
                                    <div class="text-xs">
                                      {#if event.eventType === "start"}
                                        <span
                                          class="text-green-600 dark:text-green-400"
                                        >
                                          {startTime
                                            ? `Started at ${startTime}`
                                            : "Started on this day"}
                                        </span>
                                      {:else if event.eventType === "completed"}
                                        <span class="text-muted-foreground">
                                          Completed on this day
                                        </span>
                                      {:else}
                                        <span
                                          class={level === "urgent"
                                            ? "text-red-600"
                                            : level === "hazard"
                                              ? "text-orange-600"
                                              : "text-blue-600"}
                                        >
                                          Due on this day
                                        </span>
                                        <Button
                                          size="sm"
                                          variant="outline"
                                          class="mt-2 w-full h-7 text-xs"
                                          onclick={() =>
                                            openCompletionDialog(
                                              event.instance,
                                              def,
                                              event.date
                                            )}
                                        >
                                          <Check class="h-3 w-3 mr-1" />
                                          Complete
                                        </Button>
                                      {/if}
                                    </div>
                                  </div>
                                </Popover.Content>
                              </Popover.Root>
                            {/each}
                          </div>
                        {/if}
                        <div
                          class="w-6 h-6 rounded-full border-2 border-dashed border-muted-foreground/20"
                        ></div>
                      {:else if day && "date" in day}
                        <!-- Day exists in data but has no readings -->
                        <span
                          class="absolute top-1 left-2 text-xs text-muted-foreground"
                        >
                          {new Date(day.date).getDate()}
                        </span>
                        <!-- Tracker icons for days with no readings -->
                        {#if dayTrackerEvents.length > 0}
                          <div class="absolute top-1 right-1 flex gap-0.5">
                            {#each dayTrackerEvents as event}
                              {@const def = getDefinition(
                                event.instance,
                                definitions ?? []
                              )}
                              {@const level =
                                event.eventType === "due"
                                  ? getTrackerLevel(event.instance, def)
                                  : "none"}
                              {@const category =
                                def?.category ?? TrackerCategory.Consumable}
                              {@const startTime = formatTrackerStartTime(
                                event.instance.startedAt
                              )}
                              {@const popoverId = `${event.instance.id}-${event.date}`}
                              <Popover.Root
                                open={openPopoverId === popoverId}
                                onOpenChange={(open) =>
                                  (openPopoverId = open ? popoverId : null)}
                              >
                                <Popover.Trigger>
                                  {#snippet child({ props })}
                                    <button
                                      {...props}
                                      class={cn(
                                        "h-4 w-4 rounded-full flex items-center justify-center hover:scale-125 transition-transform",
                                        getTrackerIconColor(
                                          event.eventType,
                                          level
                                        )
                                      )}
                                      title={event.instance.definitionName}
                                    >
                                      <TrackerCategoryIcon
                                        {category}
                                        class="h-3 w-3"
                                      />
                                    </button>
                                  {/snippet}
                                </Popover.Trigger>
                                <Popover.Content class="w-64 p-3" side="top">
                                  <div class="space-y-2">
                                    <div class="flex items-center gap-2">
                                      <TrackerCategoryIcon
                                        {category}
                                        class="h-4 w-4 text-muted-foreground"
                                      />
                                      <span class="font-medium text-sm">
                                        {event.instance.definitionName}
                                      </span>
                                    </div>
                                    <div class="text-xs">
                                      {#if event.eventType === "start"}
                                        <span
                                          class="text-green-600 dark:text-green-400"
                                        >
                                          {startTime
                                            ? `Started at ${startTime}`
                                            : "Started on this day"}
                                        </span>
                                      {:else if event.eventType === "completed"}
                                        <span class="text-muted-foreground">
                                          Completed on this day
                                        </span>
                                      {:else}
                                        <span
                                          class={level === "urgent"
                                            ? "text-red-600"
                                            : level === "hazard"
                                              ? "text-orange-600"
                                              : "text-blue-600"}
                                        >
                                          Due on this day
                                        </span>
                                        <Button
                                          size="sm"
                                          variant="outline"
                                          class="mt-2 w-full h-7 text-xs"
                                          onclick={() =>
                                            openCompletionDialog(
                                              event.instance,
                                              def,
                                              event.date
                                            )}
                                        >
                                          <Check class="h-3 w-3 mr-1" />
                                          Complete
                                        </Button>
                                      {/if}
                                    </div>
                                  </div>
                                </Popover.Content>
                              </Popover.Root>
                            {/each}
                          </div>
                        {/if}
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
              <div
                class="mt-4 pt-4 border-t grid grid-cols-2 md:grid-cols-4 gap-4"
              >
                <div class="text-center">
                  <div class="text-2xl font-bold text-glucose-in-range">
                    {monthSummary.inRangePercent.toFixed(1)}%
                  </div>
                  <div class="text-sm text-muted-foreground">Avg TIR</div>
                </div>
                <div class="text-center">
                  <div class="text-2xl font-bold">
                    {formatGlucoseValue(monthSummary.avgGlucose, units)}
                    <span class="text-sm font-normal text-muted-foreground">
                      {unitLabel}
                    </span>
                  </div>
                  <div class="text-sm text-muted-foreground">Avg Glucose</div>
                </div>
                <div class="text-center">
                  <div class="text-2xl font-bold">
                    {monthSummary.avgCarbs.toFixed(0)}g
                  </div>
                  <div class="text-sm text-muted-foreground">
                    Avg Daily Carbs
                  </div>
                </div>
                <div class="text-center">
                  <div class="text-2xl font-bold">
                    {monthSummary.avgInsulin.toFixed(1)}U
                  </div>
                  <div class="text-sm text-muted-foreground">
                    Avg Daily Insulin
                  </div>
                </div>
              </div>
            {/if}
          </Card.Content>
        </Card.Root>
      </div>
    {:catch}
      <!-- Tracker data failed to load - show calendar without tracker icons -->
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
    {/await}
  </div>
{:catch error}
  <div class="flex items-center justify-center h-full p-6">
    <Card.Root class="max-w-md border-destructive">
      <Card.Content class="py-8">
        <div class="text-center">
          <p class="font-medium text-destructive">
            Failed to load calendar data
          </p>
          <p class="text-sm text-muted-foreground mt-1">
            {error instanceof Error ? error.message : "An error occurred"}
          </p>
          <Button class="mt-4" onclick={() => window.location.reload()}>
            Try Again
          </Button>
        </div>
      </Card.Content>
    </Card.Root>
  </div>
{/await}

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
