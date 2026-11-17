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
    getLocalTimeZone,
    parseDate,
    today,
  } from "@internationalized/date";
  import { outsideDaySpan } from "#lib/utils/date-range.js";
  import GlucoseMonthGrid from "#lib/components/alerts/GlucoseMonthGrid.svelte";
  import GlucosePickerCell from "#lib/components/alerts/GlucosePickerCell.svelte";

  interface Props {
    /** ISO start of the confirmed range, or undefined. */
    startDate?: string;
    /** ISO end of the confirmed range, or undefined. */
    endDate?: string;
    /** Fires when a complete range is confirmed. Both args are ISO strings. */
    onRangeChange?: (start: string, end: string) => void;
    /** Days strictly after this value are disabled. Defaults to today. */
    maxDate?: string;
    /** The most days a confirmed range may cover. No limit when undefined. */
    maxDays?: number;
    /**
     * Locale tag used for week-start and weekday labels. Defaults to the user's
     * regional format, so a European format gives Monday-first weeks.
     */
    locale?: string;
  }

  let {
    startDate,
    endDate,
    onRangeChange,
    maxDate,
    maxDays,
    locale,
  }: Props = $props();

  const todayValue = today(getLocalTimeZone());
  const maxValue = $derived(maxDate ? parseDate(maxDate) : todayValue);

  // svelte-ignore state_referenced_locally
  const initialMonth = endDate ? parseDate(endDate) : todayValue;

  // Confirmed range (from props).
  const confirmedStart = $derived(startDate ? parseDate(startDate) : null);
  const confirmedEnd = $derived(endDate ? parseDate(endDate) : null);

  // In-progress selection.
  let pendingStart = $state<DateValue | null>(null);
  let hoverDate = $state<DateValue | null>(null);

  // Reset pending selection when the confirmed props change from outside.
  $effect(() => {
    void startDate;
    void endDate; // track deps
    pendingStart = null;
    hoverDate = null;
  });

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

  const isOutsideSpan = $derived(outsideDaySpan(pendingStart ?? undefined, maxDays));

  function pick(d: DateValue): void {
    if (isOutsideSpan(d)) return;
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
</script>

<GlucoseMonthGrid {initialMonth} {maxValue} {locale} onGridLeave={onMouseLeave}>
  {#snippet status()}
    {#if pendingStart}
      <span class="ml-2 text-xs text-muted-foreground font-normal">
        Select end date
      </span>
    {/if}
  {/snippet}
  {#snippet day({ date, inMonth, disabled, entries })}
    <!-- svelte-ignore a11y_no_static_element_interactions -->
    <div onmouseenter={() => onCellHover(date)}>
      <GlucosePickerCell
        {date}
        {entries}
        {inMonth}
        disabled={disabled || isOutsideSpan(date)}
        isStart={isRangeStart(date)}
        isEnd={isRangeEnd(date)}
        inRange={isInRange(date)}
        {locale}
        onclick={() => pick(date)}
      />
    </div>
  {/snippet}
</GlucoseMonthGrid>
