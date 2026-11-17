<script lang="ts">
  /**
   * Date picker laid out as a month grid where each day cell shows a mini CGM
   * trace as a backdrop (low/high regions tinted). Drop-in replacement for the
   * bits-ui `Calendar` in the alert replay popover — larger cells (~64px) so
   * the sparkline reads at a glance.
   *
   * Days outside the current month or with no readings render the day number
   * on a blank cell, still selectable.
   */
  import {
    type DateValue,
    getLocalTimeZone,
    today,
  } from "@internationalized/date";
  import GlucoseMonthGrid from "#lib/components/alerts/GlucoseMonthGrid.svelte";
  import GlucosePickerCell from "#lib/components/alerts/GlucosePickerCell.svelte";

  interface Props {
    /** Currently-selected date, or undefined for the "Last 24 hours" sentinel. */
    value?: DateValue | undefined;
    /** Fired when the user picks a day. */
    onValueChange?: (date: DateValue) => void;
    /** Disables days strictly later than this (typically `today()`). */
    maxValue?: DateValue;
    /**
     * Locale tag used for week-start and weekday labels. Defaults to the user's
     * regional format, so a European format gives Monday-first weeks.
     */
    locale?: string;
  }

  let { value, onValueChange, maxValue, locale }: Props = $props();

  // svelte-ignore state_referenced_locally
  const initialMonth = value ?? today(getLocalTimeZone());
</script>

<GlucoseMonthGrid {initialMonth} {maxValue} {locale}>
  {#snippet day({ date, inMonth, isToday, disabled, entries })}
    <GlucosePickerCell
      {date}
      {entries}
      {inMonth}
      {disabled}
      selected={value ? date.compare(value) === 0 : false}
      {isToday}
      {locale}
      onclick={() => onValueChange?.(date)}
    />
  {/snippet}
</GlucoseMonthGrid>
