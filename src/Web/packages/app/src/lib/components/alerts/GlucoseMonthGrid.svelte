<script lang="ts">
  /**
   * Month grid with per-day glucose data, shared by the single-day and range
   * pickers. Owns month paging, the weekday header and the punch card query;
   * the wrapper renders each day and owns what selecting one means.
   */
  import type { Snippet } from "svelte";
  import {
    type DateValue,
    endOfMonth,
    endOfWeek,
    getLocalTimeZone,
    startOfMonth,
    startOfWeek,
    today,
  } from "@internationalized/date";
  import ChevronLeft from "@lucide/svelte/icons/chevron-left";
  import ChevronRight from "@lucide/svelte/icons/chevron-right";
  import { Button } from "#lib/components/ui/button/index.js";
  import { getPunchCardData } from "#lib/api/generated/statistics.generated.remote.js";
  import { weekdayLabels } from "#lib/components/calendar/calendar-date.js";
  import TimeZoneNote from "#lib/components/reports/TimeZoneNote.svelte";
  import { formatLocale } from "#lib/utils/formatting.js";

  export interface GlucoseMonthGridDay {
    date: DateValue;
    inMonth: boolean;
    isToday: boolean;
    /** True for days strictly later than `maxValue`. */
    disabled: boolean;
    entries: { mills: number; mgdl: number }[];
  }

  interface Props {
    /** Month shown on mount. Later changes are ignored; the user pages. */
    initialMonth: DateValue;
    /** Days strictly later than this are disabled and cannot be paged to. */
    maxValue?: DateValue;
    /** Locale tag for week start and weekday labels. Defaults to the regional format. */
    locale?: string;
    /** Renders one day. */
    day: Snippet<[GlucoseMonthGridDay]>;
    /** Extra text in the header, before the loading note. */
    status?: Snippet;
    /** Fires when the pointer leaves the day grid. */
    onGridLeave?: () => void;
  }

  let {
    initialMonth,
    maxValue,
    locale: localeProp,
    day,
    status,
    onGridLeave,
  }: Props = $props();

  const locale = $derived(localeProp ?? formatLocale());

  const tz = getLocalTimeZone();
  const todayValue = today(tz);

  // svelte-ignore state_referenced_locally
  let viewMonth = $state<DateValue>(startOfMonth(initialMonth));

  const monthLabel = $derived(
    viewMonth.toDate(tz).toLocaleDateString(locale, {
      month: "long",
      year: "numeric",
    })
  );

  const weekdays = $derived(weekdayLabels(locale, "narrow"));

  const gridDays = $derived.by<{ date: DateValue; inMonth: boolean }[]>(() => {
    const gridStart = startOfWeek(startOfMonth(viewMonth), locale);
    const gridEnd = endOfWeek(endOfMonth(viewMonth), locale);
    const days: { date: DateValue; inMonth: boolean }[] = [];
    let cur = gridStart;
    while (cur.compare(gridEnd) <= 0) {
      days.push({
        date: cur,
        inMonth: cur.month === viewMonth.month && cur.year === viewMonth.year,
      });
      cur = cur.add({ days: 1 });
    }
    return days;
  });

  // The QueryResult is read reactively so a cached month resolves from
  // `.current` without passing through a loading state.
  type EntriesByDate = Record<string, { mills: number; mgdl: number }[]>;
  const punchCardQuery = $derived(
    getPunchCardData({
      startDate: startOfMonth(viewMonth).toString(),
      endDate: endOfMonth(viewMonth).toString(),
    })
  );
  const entriesByDate = $derived.by<EntriesByDate>(() => {
    const out: EntriesByDate = {};
    for (const m of punchCardQuery.current?.months ?? []) {
      for (const d of m.days ?? []) {
        if (d.date && d.entries && d.entries.length > 0) {
          out[d.date] = d.entries.map((e) => ({
            mills: e.mills ?? 0,
            mgdl: e.mgdl ?? 0,
          }));
        }
      }
    }
    return out;
  });

  const canGoNext = $derived(
    !maxValue || endOfMonth(viewMonth).compare(maxValue) < 0
  );

  function gotoPrevMonth(): void {
    viewMonth = startOfMonth(viewMonth).subtract({ months: 1 });
  }

  function gotoNextMonth(): void {
    viewMonth = startOfMonth(viewMonth).add({ months: 1 });
  }
</script>

<div class="p-3 w-120 max-w-[calc(100vw-2rem)]">
  <div class="flex items-center justify-between mb-2">
    <Button
      variant="ghost"
      size="icon-xs"
      onclick={gotoPrevMonth}
      aria-label="Previous month"
    >
      <ChevronLeft class="h-4 w-4" />
    </Button>
    <div class="text-sm font-medium">
      {monthLabel}
      {@render status?.()}
      {#if punchCardQuery.loading}
        <span class="ml-2 text-xs text-muted-foreground font-normal">
          loading…
        </span>
      {/if}
    </div>
    <Button
      variant="ghost"
      size="icon-xs"
      onclick={gotoNextMonth}
      disabled={!canGoNext}
      aria-label="Next month"
    >
      <ChevronRight class="h-4 w-4" />
    </Button>
  </div>

  <div class="grid grid-cols-7 gap-1 mb-1">
    {#each weekdays as label, i (i)}
      <div
        class="text-2xs text-center text-muted-foreground uppercase tracking-wide font-medium"
      >
        {label}
      </div>
    {/each}
  </div>

  {#if punchCardQuery.current?.timeZoneUnavailableReason}
    <p class="mb-2 text-sm text-muted-foreground">
      <TimeZoneNote reason={punchCardQuery.current.timeZoneUnavailableReason}>
        This calendar cannot show each day's glucose trace.
      </TimeZoneNote>
    </p>
  {/if}

  <!-- svelte-ignore a11y_no_static_element_interactions -->
  <div class="grid grid-cols-7 gap-1" onmouseleave={onGridLeave}>
    {#each gridDays as { date, inMonth } (date.toString())}
      {@render day({
        date,
        inMonth,
        isToday: date.compare(todayValue) === 0,
        disabled: maxValue ? date.compare(maxValue) > 0 : false,
        entries: entriesByDate[date.toString()] ?? [],
      })}
    {/each}
  </div>
</div>
