<script lang="ts">
  /**
   * Month-grid range date picker with per-day glucose sparklines.
   *
   * Selection flow:
   *   1. No pending start → click any day to set pendingStart.
   *   2. Pending start set → click any day at or after it to confirm the range;
   *      clicking before it resets and treats that day as the new start.
   *
   * Hover preview shows the would-be range while pendingStart is set.
   *
   * Emits `onRangeChange(start, end)` only when a complete range is confirmed.
   * Both dates are ISO strings (YYYY-MM-DD).
   */
  import {
    type DateValue,
    endOfMonth,
    endOfWeek,
    getLocalTimeZone,
    parseDate,
    startOfMonth,
    startOfWeek,
    today,
  } from "@internationalized/date";
  import { ChevronLeft, ChevronRight } from "lucide-svelte";
  import { Button } from "$lib/components/ui/button";
  import GlucosePickerCell from "$lib/components/alerts/GlucosePickerCell.svelte";
  import { getPunchCardData } from "$api/generated/statistics.generated.remote";

  interface Props {
    /** ISO start of the confirmed range, or undefined. */
    startDate?: string;
    /** ISO end of the confirmed range, or undefined. */
    endDate?: string;
    /** Fires when a complete range is confirmed. Both args are ISO strings. */
    onRangeChange?: (start: string, end: string) => void;
    /** Days strictly after this value are disabled. Defaults to today. */
    maxDate?: string;
    locale?: string;
  }

  let {
    startDate,
    endDate,
    onRangeChange,
    maxDate,
    locale = "en-US",
  }: Props = $props();

  const tz = getLocalTimeZone();
  const todayValue = today(tz);
  const maxValue = $derived(maxDate ? parseDate(maxDate) : todayValue);

  // Anchor view on the confirmed end date, or today.
  // svelte-ignore state_referenced_locally
  let viewMonth = $state<DateValue>(
    startOfMonth(endDate ? parseDate(endDate) : todayValue)
  );

  // Confirmed range (from props).
  const confirmedStart = $derived(startDate ? parseDate(startDate) : null);
  const confirmedEnd = $derived(endDate ? parseDate(endDate) : null);

  // In-progress selection.
  let pendingStart = $state<DateValue | null>(null);
  let hoverDate = $state<DateValue | null>(null);

  // Reset pending selection when the confirmed props change from outside.
  $effect(() => {
    startDate; endDate; // track deps
    pendingStart = null;
    hoverDate = null;
  });

  const monthLabel = $derived(
    viewMonth.toDate(tz).toLocaleDateString(locale, {
      month: "long",
      year: "numeric",
    })
  );

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

  const weekdayLabels = $derived.by(() => {
    const first = startOfWeek(startOfMonth(viewMonth), locale);
    const labels: string[] = [];
    for (let i = 0; i < 7; i++) {
      const d = first.add({ days: i }).toDate(tz);
      labels.push(d.toLocaleDateString(locale, { weekday: "narrow" }));
    }
    return labels;
  });

  type EntriesByDate = Record<string, { mills: number; mgdl: number }[]>;
  const entriesPromise = $derived.by<Promise<EntriesByDate>>(() => {
    const startDate = startOfMonth(viewMonth).toDate(tz);
    const endDate = endOfMonth(viewMonth).toDate(tz);
    return getPunchCardData({ startDate, endDate }).then((data) => {
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
    return d.compare(maxValue) > 0;
  }

  function dateKey(d: DateValue): string {
    return d.toString();
  }

  // Effective range to highlight — either the confirmed range or the
  // in-progress preview (pendingStart + hover).
  const rangeStart = $derived.by<DateValue | null>(() => {
    if (pendingStart) {
      if (hoverDate && hoverDate.compare(pendingStart) >= 0) return pendingStart;
      if (hoverDate && hoverDate.compare(pendingStart) < 0) return hoverDate;
      return pendingStart;
    }
    return confirmedStart;
  });

  const rangeEnd = $derived.by<DateValue | null>(() => {
    if (pendingStart) {
      if (hoverDate && hoverDate.compare(pendingStart) >= 0) return hoverDate;
      if (hoverDate && hoverDate.compare(pendingStart) < 0) return pendingStart;
      return null;
    }
    return confirmedEnd;
  });

  function isRangeStart(d: DateValue): boolean {
    return rangeStart ? d.compare(rangeStart) === 0 : false;
  }

  function isRangeEnd(d: DateValue): boolean {
    return rangeEnd ? d.compare(rangeEnd) === 0 : false;
  }

  function isInRange(d: DateValue): boolean {
    if (!rangeStart || !rangeEnd) return false;
    return d.compare(rangeStart) > 0 && d.compare(rangeEnd) < 0;
  }

  function pick(d: DateValue): void {
    if (isFuture(d)) return;

    if (!pendingStart) {
      // Start a new selection.
      pendingStart = d;
      return;
    }

    if (d.compare(pendingStart) < 0) {
      // Clicked before the pending start — treat as new start.
      pendingStart = d;
      return;
    }

    if (d.compare(pendingStart) === 0) {
      // Clicked the same day — cancel selection.
      pendingStart = null;
      return;
    }

    // Confirmed range.
    onRangeChange?.(pendingStart.toString(), d.toString());
    pendingStart = null;
    hoverDate = null;
  }

  function onCellHover(d: DateValue): void {
    if (pendingStart) hoverDate = d;
  }

  function onMouseLeave(): void {
    hoverDate = null;
  }

  const canGoNext = $derived(endOfMonth(viewMonth).compare(maxValue) < 0);

  function gotoPrevMonth(): void {
    viewMonth = startOfMonth(viewMonth).subtract({ months: 1 });
  }

  function gotoNextMonth(): void {
    viewMonth = startOfMonth(viewMonth).add({ months: 1 });
  }
</script>

{#snippet grid(entriesByDate: EntriesByDate)}
  <!-- svelte-ignore a11y_no_static_element_interactions -->
  <div class="grid grid-cols-7 gap-1" onmouseleave={onMouseLeave}>
    {#each gridDays as { date, inMonth } (dateKey(date))}
      {@const entries = entriesByDate[dateKey(date)] ?? []}
      <div onmouseenter={() => onCellHover(date)}>
        <GlucosePickerCell
          {date}
          {entries}
          {inMonth}
          disabled={isFuture(date)}
          isStart={isRangeStart(date)}
          isEnd={isRangeEnd(date)}
          inRange={isInRange(date)}
          {locale}
          onclick={() => pick(date)}
        />
      </div>
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
      {#if pendingStart}
        <span class="ml-2 text-xs text-muted-foreground font-normal">
          Click end date
        </span>
      {/if}
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
      <div class="text-[10px] text-center text-muted-foreground uppercase tracking-wide font-medium">
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
