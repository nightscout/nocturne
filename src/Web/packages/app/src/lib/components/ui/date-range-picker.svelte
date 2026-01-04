<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import { getLocalTimeZone, parseDate, today } from "@internationalized/date";
  import type { DateRange } from "bits-ui";
  import { queryParam } from "sveltekit-search-params";

  import RangeCalendar from "$lib/components/ui/range-calendar/range-calendar.svelte";
  import * as Popover from "$lib/components/ui/popover/index.js";
  import ChevronDownIcon from "@lucide/svelte/icons/chevron-down";

  interface Props {
    showDaysPresets?: boolean;
    defaultDays?: number;
    onDateChange?: (params: {
      from?: string;
      to?: string;
      days?: number;
    }) => void;
  }

  let {
    showDaysPresets = true,
    defaultDays = 7,
    onDateChange,
  }: Props = $props();

  // URL search params using sveltekit-search-params
  const days = queryParam("days", {
    encode: (value: number | undefined) => value?.toString() ?? "",
    decode: (value: string | null) => {
      if (!value) return undefined;
      const parsed = parseInt(value);
      return isNaN(parsed) ? undefined : parsed;
    },
    defaultValue: undefined,
  });

  const fromDate = queryParam("from", {
    encode: (value: string | undefined) => value ?? "",
    decode: (value: string | null) => value || undefined,
    defaultValue: undefined,
  });

  const toDate = queryParam("to", {
    encode: (value: string | undefined) => value ?? "",
    decode: (value: string | null) => value || undefined,
    defaultValue: undefined,
  });

  let value = $state<DateRange | undefined>();
  let open = $state(false);
  let id = `date-range-${Math.random().toString(36).substr(2, 9)}`;
  let initialized = $state(false);

  // Derived state for selected days (for UI highlighting)
  const selectedDays = $derived($days);

  // Initialize and sync state with URL parameters
  $effect(() => {
    if (!initialized) {
      initializeFromURL();
      initialized = true;
    } else {
      // Watch for URL parameter changes after initialization
      syncStateWithURL();
    }
  });

  function initializeFromURL() {
    updateComponentStateFromURL();
  }

  function syncStateWithURL() {
    updateComponentStateFromURL();
  }

  function updateComponentStateFromURL() {
    if ($days) {
      // Handle days parameter (shorthand for X days ago to today)
      const endDate = today(getLocalTimeZone());
      const startDate = endDate.subtract({ days: $days - 1 });

      value = { start: startDate, end: endDate };

      // Call the callback
      onDateChange?.({
        from: startDate.toString(),
        to: endDate.toString(),
        days: $days,
      });
    } else if ($fromDate && $toDate) {
      // Handle explicit date range
      try {
        const startDate = parseDate($fromDate);
        const endDate = parseDate($toDate);

        // Only update if the dates are actually different
        if (
          !value ||
          value.start?.compare(startDate) !== 0 ||
          value.end?.compare(endDate) !== 0
        ) {
          value = { start: startDate, end: endDate };

          // Call the callback for explicit date ranges
          onDateChange?.({
            from: startDate.toString(),
            to: endDate.toString(),
            days: undefined,
          });
        }
      } catch (error) {
        console.warn("Failed to parse date range from URL:", error);
        setDefaultRange();
      }
    } else if (!initialized) {
      // No URL parameters, use default (only on first load)
      setDefaultRange();
    }
  }

  function setDefaultRange() {
    setDayRange(defaultDays);
  }

  function setDayRange(daysCount: number) {
    const endDate = today(getLocalTimeZone());
    const startDate = endDate.subtract({ days: daysCount - 1 });

    value = { start: startDate, end: endDate };

    // Update URL parameters using sveltekit-search-params
    days.set(daysCount);
    fromDate.set(startDate.toString());
    toDate.set(endDate.toString());

    // Call the callback
    onDateChange?.({
      from: startDate.toString(),
      to: endDate.toString(),
      days: daysCount,
    });
  }

  function handleCalendarChange(newValue: DateRange | undefined) {
    if (newValue?.start && newValue?.end) {
      // Clear days parameter when manually selecting dates
      days.set(undefined);
      fromDate.set(newValue.start.toString());
      toDate.set(newValue.end.toString());

      // Call the callback
      onDateChange?.({
        from: newValue.start.toString(),
        to: newValue.end.toString(),
        days: undefined,
      });
    }
  }
</script>

<div class="flex flex-wrap items-center justify-between gap-4">
  <!-- Date Range Calendar (left) -->
  <Popover.Root bind:open>
    <Popover.Trigger id="{id}-dates">
      {#snippet child({ props })}
        <Button
          {...props}
          variant="outline"
          class="w-56 justify-between font-normal"
        >
          {value?.start && value?.end
            ? `${value.start.toDate(getLocalTimeZone()).toLocaleDateString()} - ${value.end.toDate(getLocalTimeZone()).toLocaleDateString()}`
            : "Select date"}
          <ChevronDownIcon />
        </Button>
      {/snippet}
    </Popover.Trigger>
    <Popover.Content class="w-auto overflow-hidden p-0" align="start">
      <RangeCalendar
        bind:value
        captionLayout="dropdown"
        onValueChange={handleCalendarChange}
      />
    </Popover.Content>
  </Popover.Root>

  {#if showDaysPresets}
    <!-- Quick Day Selection (right) -->
    <div class="flex flex-wrap gap-2">
      {#each [1, 3, 7, 14, 30, 90] as daysOption}
        <Button
          variant={selectedDays === daysOption ? "default" : "outline"}
          size="sm"
          onclick={() => setDayRange(daysOption)}
          class="text-xs"
        >
          {daysOption === 1 ? "Today" : `${daysOption} days`}
        </Button>
      {/each}
    </div>
  {/if}
</div>
