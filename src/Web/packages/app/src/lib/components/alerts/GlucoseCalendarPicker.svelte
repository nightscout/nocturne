<script lang="ts">
  /**
   * Date picker laid out as a month grid where each day cell shows a mini CGM
   * trace as a backdrop (low/high regions tinted). Drop-in replacement for the
   * bits-ui `Calendar` in the alert replay popover — larger cells (~64px) so
   * the sparkline reads at a glance.
   *
   * Data is fetched per visible month via `getPunchCardData` and keyed by
   * YYYY-MM-DD; days outside the current month or with no readings render the
   * day number on a blank cell, still selectable.
   */
  import {
    type DateValue,
    endOfMonth,
    endOfWeek,
    getLocalTimeZone,
    startOfMonth,
    startOfWeek,
    today,
  } from "@internationalized/date";
  import { ChevronLeft, ChevronRight } from "lucide-svelte";
  import { Button } from "$lib/components/ui/button";
  import { cn } from "$lib/utils";
  import GlucoseSparkline from "$lib/components/calendar/GlucoseSparkline.svelte";
  import { getPunchCardData } from "$api/month-to-month.remote";

  interface Props {
    /** Currently-selected date, or undefined for the "Last 24 hours" sentinel. */
    value?: DateValue | undefined;
    /** Fired when the user picks a day. */
    onValueChange?: (date: DateValue) => void;
    /** Disables days strictly later than this (typically `today()`). */
    maxValue?: DateValue;
    /**
     * Locale tag used for week-start and weekday labels. Defaults to en-US
     * (Sunday-first).
     */
    locale?: string;
  }

  let { value, onValueChange, maxValue, locale = "en-US" }: Props = $props();

  const tz = getLocalTimeZone();
  const todayValue = today(tz);

  // Anchor the visible month on the selected date when we have one, otherwise
  // on today. svelte-ignore: deliberate snapshot at mount; user navigates with
  // the prev/next buttons.
  // svelte-ignore state_referenced_locally
  let viewMonth = $state<DateValue>(startOfMonth(value ?? todayValue));

  const monthLabel = $derived(
    viewMonth.toDate(tz).toLocaleDateString(locale, {
      month: "long",
      year: "numeric",
    })
  );

  // Build a 6×7 grid that includes leading/trailing days from the neighbour
  // months so the layout stays rectangular.
  const gridDays = $derived.by<{ date: DateValue; inMonth: boolean }[]>(() => {
    const monthStart = startOfMonth(viewMonth);
    const monthEnd = endOfMonth(viewMonth);
    const gridStart = startOfWeek(monthStart, locale);
    const gridEnd = endOfWeek(monthEnd, locale);
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

  // Generate the localised weekday header from the grid's first row so it
  // tracks `locale` (Sunday- vs. Monday-first).
  const weekdayLabels = $derived.by(() => {
    const first = startOfWeek(startOfMonth(viewMonth), locale);
    const labels: string[] = [];
    for (let i = 0; i < 7; i++) {
      const d = first.add({ days: i }).toDate(tz);
      labels.push(d.toLocaleDateString(locale, { weekday: "narrow" }));
    }
    return labels;
  });

  // Derived promise of per-day entries keyed by YYYY-MM-DD. Re-fetches
  // automatically when `viewMonth` changes; the template's {#await} block
  // shows a loading badge until each new request resolves. `getPunchCardData`
  // returns the same shape as the calendar page consumes, so we just project
  // out the `entries` array per day.
  type EntriesByDate = Record<string, { mills: number; mgdl: number }[]>;
  const entriesPromise = $derived.by<Promise<EntriesByDate>>(() => {
    const fromDate = startOfMonth(viewMonth).toString();
    const toDate = endOfMonth(viewMonth).toString();
    return getPunchCardData({ fromDate, toDate }).then((data) => {
      const out: EntriesByDate = {};
      for (const m of data?.months ?? []) {
        for (const d of m.days ?? []) {
          if (d.date && d.entries && d.entries.length > 0) {
            out[d.date] = d.entries;
          }
        }
      }
      return out;
    });
  });

  function isFuture(d: DateValue): boolean {
    return maxValue ? d.compare(maxValue) > 0 : false;
  }

  function isSelected(d: DateValue): boolean {
    return value ? d.compare(value) === 0 : false;
  }

  function isTodayCell(d: DateValue): boolean {
    return d.compare(todayValue) === 0;
  }

  function dateKey(d: DateValue): string {
    return d.toString();
  }

  function pick(d: DateValue): void {
    if (isFuture(d)) return;
    onValueChange?.(d);
  }

  function gotoPrevMonth(): void {
    viewMonth = startOfMonth(viewMonth).subtract({ months: 1 });
  }

  function gotoNextMonth(): void {
    viewMonth = startOfMonth(viewMonth).add({ months: 1 });
  }

  // Block paging into months that are entirely future.
  const canGoNext = $derived(
    !maxValue || endOfMonth(viewMonth).compare(maxValue) < 0
  );
</script>

{#snippet grid(
  entriesByDate: Record<string, { mills: number; mgdl: number }[]>
)}
  <div class="grid grid-cols-7 gap-1">
    {#each gridDays as { date, inMonth } (dateKey(date))}
      {@const entries = entriesByDate[dateKey(date)] ?? []}
      {@const future = isFuture(date)}
      {@const selected = isSelected(date)}
      {@const isToday = isTodayCell(date)}
      <button
        type="button"
        disabled={future}
        onclick={() => pick(date)}
        aria-label={date.toDate(tz).toLocaleDateString(locale, {
          weekday: "long",
          year: "numeric",
          month: "long",
          day: "numeric",
        })}
        aria-pressed={selected}
        class={cn(
          "relative h-16 rounded-md border bg-background overflow-hidden transition-colors",
          "focus:outline-none focus:ring-2 focus:ring-primary focus:ring-inset",
          inMonth ? "border-border/60" : "border-transparent opacity-40",
          future && "cursor-not-allowed opacity-30",
          !future && "hover:bg-muted/40",
          selected && "ring-2 ring-primary border-primary",
          isToday && !selected && "ring-1 ring-primary/60"
        )}
      >
        <span
          class={cn(
            "absolute top-1 left-1.5 text-[10px] font-medium z-10 tabular-nums",
            inMonth ? "text-foreground" : "text-muted-foreground",
            selected && "text-primary"
          )}
        >
          {date.day}
        </span>
        {#if entries.length > 0}
          <div class="absolute inset-0 pt-3.5 pointer-events-none">
            <GlucoseSparkline {entries} />
          </div>
        {/if}
      </button>
    {/each}
  </div>
{/snippet}

<div class="p-3 w-[480px]">
  <div class="flex items-center justify-between mb-2">
    <Button
      variant="ghost"
      size="icon"
      class="h-7 w-7"
      onclick={gotoPrevMonth}
      aria-label="Previous month"
    >
      <ChevronLeft class="h-4 w-4" />
    </Button>
    <div class="text-sm font-medium">
      {monthLabel}
      {#await entriesPromise}
        <span class="ml-2 text-xs text-muted-foreground font-normal">
          loading…
        </span>
      {/await}
    </div>
    <Button
      variant="ghost"
      size="icon"
      class="h-7 w-7"
      onclick={gotoNextMonth}
      disabled={!canGoNext}
      aria-label="Next month"
    >
      <ChevronRight class="h-4 w-4" />
    </Button>
  </div>

  <div class="grid grid-cols-7 gap-1 mb-1">
    {#each weekdayLabels as label, i (i)}
      <div
        class="text-[10px] text-center text-muted-foreground uppercase tracking-wide font-medium"
      >
        {label}
      </div>
    {/each}
  </div>

  {#await entriesPromise}
    {@render grid({})}
  {:then entriesByDate}
    {@render grid(entriesByDate)}
  {:catch}
    {@render grid({})}
  {/await}
</div>
