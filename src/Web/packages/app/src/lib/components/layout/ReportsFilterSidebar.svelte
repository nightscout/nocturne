<script lang="ts">
  import * as Sheet from "$lib/components/ui/sheet";
  import { Button } from "$lib/components/ui/button";
  import { Label } from "$lib/components/ui/label";
  import { Separator } from "$lib/components/ui/separator";
  import { ScrollArea } from "$lib/components/ui/scroll-area";
  import { Switch } from "$lib/components/ui/switch";
  import { getLocalTimeZone, parseDate, today } from "@internationalized/date";
  import type { DateRange } from "bits-ui";
  import { queryParam } from "sveltekit-search-params";
  import RangeCalendar from "$lib/components/ui/range-calendar/range-calendar.svelte";
  import { Calendar, Filter, RotateCcw } from "lucide-svelte";

  interface Props {
    open?: boolean;
    onOpenChange?: (open: boolean) => void;
  }

  let { open = $bindable(false), onOpenChange }: Props = $props();

  // URL search params
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
  let initialized = $state(false);

  // Quick day presets
  const dayPresets = [
    { label: "Today", days: 1 },
    { label: "3 Days", days: 3 },
    { label: "7 Days", days: 7 },
    { label: "14 Days", days: 14 },
    { label: "30 Days", days: 30 },
    { label: "90 Days", days: 90 },
  ];

  // Derived state for selected days (for UI highlighting)
  const selectedDays = $derived($days);

  // Initialize state from URL
  $effect(() => {
    if (!initialized) {
      initializeFromURL();
      initialized = true;
    }
  });

  function initializeFromURL() {
    if ($days) {
      const endDate = today(getLocalTimeZone());
      const startDate = endDate.subtract({ days: $days - 1 });
      value = { start: startDate, end: endDate };
    } else if ($fromDate && $toDate) {
      try {
        const startDate = parseDate($fromDate);
        const endDate = parseDate($toDate);
        value = { start: startDate, end: endDate };
      } catch (error) {
        console.warn("Failed to parse date range from URL:", error);
        setDayRange(7);
      }
    } else {
      setDayRange(7);
    }
  }

  function setDayRange(daysCount: number) {
    const endDate = today(getLocalTimeZone());
    const startDate = endDate.subtract({ days: daysCount - 1 });

    value = { start: startDate, end: endDate };

    days.set(daysCount);
    fromDate.set(startDate.toString());
    toDate.set(endDate.toString());
  }

  function handleCalendarChange(newValue: DateRange | undefined) {
    if (newValue?.start && newValue?.end) {
      days.set(undefined);
      fromDate.set(newValue.start.toString());
      toDate.set(newValue.end.toString());
    }
  }

  function resetFilters() {
    setDayRange(7);
  }

  function closeSheet() {
    open = false;
    onOpenChange?.(false);
  }

  // Get formatted date range for display
  const dateRangeText = $derived(() => {
    if (value?.start && value?.end) {
      const start = value.start.toDate(getLocalTimeZone());
      const end = value.end.toDate(getLocalTimeZone());
      return `${start.toLocaleDateString()} - ${end.toLocaleDateString()}`;
    }
    return "Select dates";
  });
</script>

<Sheet.Root bind:open {onOpenChange}>
  <Sheet.Content side="right" class="w-[320px] sm:w-[400px] p-0">
    <Sheet.Header class="px-6 py-4 border-b border-border">
      <div class="flex items-center justify-between">
        <Sheet.Title class="flex items-center gap-2">
          <Filter class="h-5 w-5" />
          Report Filters
        </Sheet.Title>
      </div>
      <Sheet.Description class="text-sm text-muted-foreground">
        Adjust the date range and filters for your report.
      </Sheet.Description>
    </Sheet.Header>

    <ScrollArea class="h-[calc(100vh-180px)]">
      <div class="px-6 py-4 space-y-6">
        <!-- Quick Date Presets -->
        <div class="space-y-3">
          <Label class="text-sm font-medium">Quick Selection</Label>
          <div class="grid grid-cols-3 gap-2">
            {#each dayPresets as preset}
              <Button
                variant={selectedDays === preset.days ? "default" : "outline"}
                size="sm"
                onclick={() => setDayRange(preset.days)}
                class="text-xs"
              >
                {preset.label}
              </Button>
            {/each}
          </div>
        </div>

        <Separator />

        <!-- Calendar Selection -->
        <div class="space-y-3">
          <Label class="text-sm font-medium flex items-center gap-2">
            <Calendar class="h-4 w-4" />
            Custom Date Range
          </Label>
          <div class="text-sm text-muted-foreground mb-2">
            {dateRangeText()}
          </div>
          <div class="border border-border rounded-lg overflow-hidden">
            <RangeCalendar
              bind:value
              captionLayout="dropdown"
              onValueChange={handleCalendarChange}
              class="p-0"
            />
          </div>
        </div>

        <Separator />

        <!-- Additional Filters (placeholders for future features) -->
        <div class="space-y-3">
          <Label class="text-sm font-medium">Display Options</Label>

          <div class="flex items-center justify-between">
            <Label class="text-sm text-muted-foreground">
              Show target range
            </Label>
            <Switch checked={true} />
          </div>

          <div class="flex items-center justify-between">
            <Label class="text-sm text-muted-foreground">Show treatments</Label>
            <Switch checked={true} />
          </div>

          <div class="flex items-center justify-between">
            <Label class="text-sm text-muted-foreground">Include notes</Label>
            <Switch checked={false} />
          </div>
        </div>
      </div>
    </ScrollArea>

    <div
      class="absolute bottom-0 left-0 right-0 p-4 border-t border-border bg-background"
    >
      <div class="flex gap-2">
        <Button variant="outline" class="flex-1" onclick={resetFilters}>
          <RotateCcw class="h-4 w-4 mr-2" />
          Reset
        </Button>
        <Button class="flex-1" onclick={closeSheet}>Apply Filters</Button>
      </div>
    </div>
  </Sheet.Content>
</Sheet.Root>
