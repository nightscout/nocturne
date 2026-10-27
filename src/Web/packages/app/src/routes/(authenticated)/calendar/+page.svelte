<script lang="ts">
  import { goto } from "$app/navigation";
  import { resolve } from "$app/paths";
  import { page } from "$app/state";
  import { startQueries } from "#lib/utils/start-queries.js";
  import * as Card from "#lib/components/ui/card/index.js";
  import { getPunchCardData } from "#lib/api/generated/statistics.generated.remote.js";
  import { remoteErrorMessage } from "#lib/api/remote-error.js";
  import { getActiveInstances, getDefinitions, getInstanceHistory } from "#lib/api/generated/trackers.generated.remote.js";
  import type { TrackerInstanceDto, TrackerDefinitionDto } from "#lib/api/index.js";
  import { reachedUrgency } from "#lib/components/trackers/schedule.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { calendarShowTirPercent, glucoseUnits } from "#lib/stores/appearance-store.svelte.js";
  import { getUnitLabel, formatLocale, formatMediumDateTime, time } from "#lib/utils/formatting.js";
  import {
    leadingBlankDays,
    weekdayLabels,
  } from "#lib/components/calendar/calendar-date.js";
  import CalendarSkeleton from "#lib/components/calendar/CalendarSkeleton.svelte";
  import { TrackerCompletionDialog } from "#lib/components/trackers/index.js";
  import CalendarHeader from "#lib/components/calendar/CalendarHeader.svelte";
  import CalendarMonthSummary from "#lib/components/calendar/CalendarMonthSummary.svelte";
  import CalendarDayCell from "#lib/components/calendar/CalendarDayCell.svelte";
  import TimeZoneNote from "#lib/components/reports/TimeZoneNote.svelte";
  import { coachmark } from "@nocturne/coach";
  import { coachFor } from "#lib/coach-marks/copy.svelte.js";
  import { groupBy, indexBy } from "#lib/utils/collections.js";
  import { toDayString } from "#lib/utils/date-range.js";

  // Infer DayStats type from the query result
  type PunchCardMonth = NonNullable<
    NonNullable<Awaited<ReturnType<typeof getPunchCardData>>>["months"]
  >[number];
  type DayStats = NonNullable<PunchCardMonth["days"]>[number];

  // View mode: 'tir' for Time in Range bars, 'profile' for glucose line charts
  type ViewMode = "tir" | "profile";
  function isViewMode(value: string | null): value is ViewMode {
    return value === "tir" || value === "profile";
  }
  const storedViewMode =
    typeof localStorage !== "undefined" ? localStorage.getItem("calendar-view-mode") : null;
  let viewMode = $state<ViewMode>(isViewMode(storedViewMode) ? storedViewMode : "tir");

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
  // Timezone-free strings keep the hydration key stable across SSR/client timezones.
  // The endpoint takes these as calendar dates in the tenant's timezone.
  const dateRangeInput = $derived.by(() => {
    const month = `${currentYear}-${String(currentMonth + 1).padStart(2, "0")}`;
    const lastDay = new Date(currentYear, currentMonth + 1, 0).getDate();
    return {
      startDate: `${month}-01`,
      endDate: `${month}-${String(lastDay).padStart(2, "0")}`,
    };
  });

  // Query responses
  const punchCardQuery = $derived(getPunchCardData(dateRangeInput));
  const trackersQuery = getActiveInstances();
  const historyQuery = getInstanceHistory({ limit: 100 });
  const definitionsQuery = getDefinitions({});
  // Only the first month's query needs starting with the others; the template reads every later one.
  // svelte-ignore state_referenced_locally
  startQueries(punchCardQuery, trackersQuery, historyQuery, definitionsQuery);

  // Tracker event types
  type TrackerEventType = "start" | "due" | "completed";
  interface TrackerEvent {
    instance: TrackerInstanceDto;
    eventType: TrackerEventType;
    date: string; // YYYY-MM-DD
  }

  // Navigation functions
  function updateUrl(date: Date) {
    const url = new URL(page.url.href);
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
    // eslint-disable-next-line svelte/no-navigation-without-resolve -- the current page's URL with its month params changed, already resolved
    goto(url.toString(), { refreshAll: true });
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

    return today.getMonth() === currentMonth && today.getFullYear() === currentYear;
  });

  const units = $derived(glucoseUnits.current);
  const unitLabel = $derived(getUnitLabel(units));

  // Column headings and month names follow the regional format, so a European
  // format renders Monday-first weeks with its own weekday and month names.
  // 3 chars keeps the seven columns even; English already abbreviates to that.
  const DAY_NAMES = $derived(weekdayLabels(formatLocale(), "short", 3));
  const MONTH_NAMES = $derived.by(() => {
    const format = new Intl.DateTimeFormat(formatLocale(), { month: "long" });
    return Array.from({ length: 12 }, (_, m) => format.format(new Date(2026, m, 1)));
  });

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
    return {
      days: indexBy(monthData?.days ?? [], (day) => day.date, (day) => day),
      maxCarbs: monthData?.maxCarbs ?? 0,
      maxInsulin: monthData?.maxInsulin ?? 0,
      maxDiff: monthData?.maxCarbInsulinDiff ?? 0,
    };
  });

  const calendarGrid = $derived.by(() => {
    const lastDay = new Date(currentYear, currentMonth + 1, 0);
    const daysInMonth = lastDay.getDate();
    const startDayOfWeek = leadingBlankDays(currentYear, currentMonth, formatLocale());

    const grid: (DayStats | null | { empty: true; dayNumber?: number })[][] =
      [];
    let currentDay = 1;

    for (let week = 0; week < 6; week++) {
      const weekDays: (DayStats | null | { empty: true; dayNumber?: number })[] = [];

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
    goto(resolve(`reports/day-in-review?date=${day.date}`));
  }

  const monthSummary = $derived(
    punchCardQuery.current?.months?.find(
      (m) => m.year === currentYear && m.month === currentMonth
    )?.summary
  );

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

  function getTrackerLevel(instance: TrackerInstanceDto): string {
    return reachedUrgency(instance, Date.now())?.toLowerCase() ?? "none";
  }

  function buildTrackerEvents(
    active: TrackerInstanceDto[],
    history: TrackerInstanceDto[]
  ): Map<string, TrackerEvent[]> {
    const event = (
      instance: TrackerInstanceDto,
      eventType: TrackerEventType,
      at: string | undefined
    ): TrackerEvent[] =>
      at ? [{ instance, eventType, date: toDayString(new Date(at)) }] : [];
    return groupBy(
      [
        ...active.flatMap((i) => [
          ...event(i, "start", i.startedAt),
          ...event(i, "due", i.expectedEndAt),
        ]),
        ...history.flatMap((i) => event(i, "completed", i.completedAt)),
      ],
      (e) => e.date
    );
  }

  /**
   * The tone a tracker icon is drawn in, emitted as a data attribute so the
   * colour itself lives in CSS (see CalendarDayCell).
   */
  function getTrackerTone(eventType: string, level: string): string {
    if (eventType === "completed") return "completed";
    if (eventType === "start") return "start";
    return level;
  }

  function formatTrackerStartTime(startedAt: string | undefined): string | null {
    if (!startedAt) return null;
    const date = new Date(startedAt);
    if (Number.isNaN(date.getTime())) return null;
    return time(date);
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
    // eslint-disable-next-line svelte/no-navigation-without-resolve -- reloads the current page's URL, already resolved
    goto(page.url.toString(), { refreshAll: true });
  }
</script>

{#if punchCardLoading}
  <CalendarSkeleton />
{:else if punchCardError}
  <div class="flex items-center justify-center h-full p-6">
    <Card.Root variant="destructive" class="max-w-md">
      <Card.Content class="py-8">
        <div class="text-center">
          <p class="font-medium text-destructive">
            Failed to load calendar data
          </p>
          <p class="text-sm text-muted-foreground mt-1">
            {remoteErrorMessage(punchCardError, "An error occurred")}
          </p>
          <Button class="mt-4" onclick={() => window.location.reload()}>
            Try again
          </Button>
        </div>
      </Card.Content>
    </Card.Root>
  </div>
{:else}
  <div class="@container flex flex-col h-full">
    <div class="hidden print:block border-b pb-3 mb-4">
      <h1 class="text-xl font-bold">
        Month-to-month report: {MONTH_NAMES[currentMonth]}
        {currentYear}
      </h1>
      <p class="text-sm text-muted-foreground">
        Generated {formatMediumDateTime(new Date())}
      </p>
    </div>

    <div
      class="sticky top-(--app-sticky-top,0px) z-10 transition-all duration-300 print:hidden"
      {@attach coachmark({
        ...coachFor("feature-intro.calendar-views"),
        completeOn: { event: "click" },
      })}
    >
      <CalendarHeader
        viewDate={viewDate}
        bind:viewMode
        isCurrentMonth={isCurrentMonth}
        MONTH_NAMES={MONTH_NAMES}
        previousMonth={previousMonth}
        nextMonth={nextMonth}
        goToToday={goToToday}
        setViewMode={setViewMode}
        showTirPercent={calendarShowTirPercent.current}
        setShowTirPercent={(show) => calendarShowTirPercent.current = show}
      />
    </div>

    {#if trackersLoading}
      <div class="flex-1 p-3 @md:p-4">
        <Card.Root class="h-full">
          <Card.Content class="p-4 h-full flex items-center justify-center">
            <div class="text-muted-foreground">Loading data...</div>
          </Card.Content>
        </Card.Root>
      </div>
    {:else}
      <div class="flex-1 p-3 @md:p-4">
        <Card.Root class="h-full">
          <Card.Content class="p-2 @md:p-4 h-full flex flex-col">
            {#if trackersError}
              <!-- Trackers overlay the calendar; losing them must not take the glucose
                   calendar with them, which is served by a separate query. -->
              <p class="text-sm text-muted-foreground pb-2">
                Tracker data is unavailable, so site and sensor changes are not shown.
              </p>
            {/if}
            {#if punchCardQuery.current?.timeZoneUnavailableReason}
              <p class="text-sm text-muted-foreground">
                <TimeZoneNote reason={punchCardQuery.current.timeZoneUnavailableReason}>
                  This report cannot show each day's time in range, glucose and insulin.
                </TimeZoneNote>
              </p>
            {/if}
            <div class="flex-1 overflow-x-auto print:overflow-visible flex flex-col">
              <div class="grid grid-cols-7 gap-1 mb-2 min-w-[28rem] @md:min-w-0">
                {#each DAY_NAMES as dayName, i (i)}
                  <div
                    class="text-center text-sm font-medium text-muted-foreground py-2"
                  >
                    {dayName}
                  </div>
                {/each}
              </div>

              <div
                class="flex-1 grid grid-rows-6 gap-1 min-w-[28rem] @md:min-w-0"
                {@attach coachmark({
                  ...coachFor("feature-intro.calendar-trackers"),
                })}
              >
                {#each calendarGrid as week, idx (idx)}
                  <div class="grid grid-cols-7 gap-1">
                    {#each week as day, i (i)}
                      <CalendarDayCell
                        day={day}
                        viewMode={viewMode}
                        showTirPercent={calendarShowTirPercent.current}
                        currentYear={currentYear}
                        currentMonth={currentMonth}
                        trackerEvents={trackerEvents}
                        definitions={definitions}
                        bind:openPopoverId
                        units={units}
                        unitLabel={unitLabel}
                        handleDayClick={handleDayClick}
                        getDefinition={getDefinition}
                        getTrackerLevel={getTrackerLevel}
                        getTrackerTone={getTrackerTone}
                        formatTrackerStartTime={formatTrackerStartTime}
                        formatTrackerAge={formatTrackerAge}
                        openCompletionDialog={openCompletionDialog}
                      />
                    {/each}
                  </div>
                {/each}
              </div>
            </div>

            <CalendarMonthSummary
              monthSummary={monthSummary}
              units={units}
              unitLabel={unitLabel}
            />
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
