<script lang="ts">
  import * as Sheet from "$lib/components/ui/sheet";
  import { Button } from "$lib/components/ui/button";
  import { Label } from "$lib/components/ui/label";
  import { Separator } from "$lib/components/ui/separator";
  import { ScrollArea } from "$lib/components/ui/scroll-area";
  import { Switch } from "$lib/components/ui/switch";
  import { getLocalTimeZone, parseDate, today } from "@internationalized/date";
  import type { DateRange } from "bits-ui";
  import { requireDateParamsContext } from "$lib/hooks/date-params.svelte";
  import { RangeCalendar } from "$lib/components/ui/range-calendar";
  import { Calendar, Filter, RotateCcw } from "lucide-svelte";

  interface Props {
    open?: boolean;
    onOpenChange?: (open: boolean) => void;
  }

  let { open = $bindable(false), onOpenChange }: Props = $props();

  // Get shared date params from context (set by reports layout)
  const params = requireDateParamsContext();

  // Quick day presets
  const dayPresets = [
    { label: "Today", days: 1 },
    { label: "3 Days", days: 3 },
    { label: "7 Days", days: 7 },
    { label: "14 Days", days: 14 },
    { label: "30 Days", days: 30 },
    { label: "90 Days", days: 90 },
  ];

  const MS_PER_DAY = 86_400_000;

  // === DRAFT STATE ===
  // The pending date range selection before clicking "Apply Filters".
  // This is the single source of truth; the highlighted preset is derived from it.
  let draftCalendarValue = $state<DateRange | undefined>(undefined);

  // Initialize draft state when sidebar opens
  $effect(() => {
    if (!open) return;

    if (params.from && params.to) {
      try {
        draftCalendarValue = {
          start: parseDate(params.from),
          end: parseDate(params.to),
        };
        return;
      } catch {
        // Fall through to days-based calculation
      }
    }

    if (params.days) {
      const endDate = today(getLocalTimeZone());
      draftCalendarValue = {
        start: endDate.subtract({ days: params.days - 1 }),
        end: endDate,
      };
    }
  });

  // The quick-selection preset to highlight: the whole-day span of the draft
  // range when it ends today and matches a preset, otherwise none. Derived from
  // the range itself so it stays correct whether set via preset or calendar.
  const selectedDays = $derived.by(() => {
    const start = draftCalendarValue?.start;
    const end = draftCalendarValue?.end;
    if (!start || !end) return undefined;

    const tz = getLocalTimeZone();
    if (end.compare(today(tz)) !== 0) return undefined;

    const days =
      Math.round(
        (end.toDate(tz).getTime() - start.toDate(tz).getTime()) / MS_PER_DAY
      ) + 1;
    return dayPresets.some((p) => p.days === days) ? days : undefined;
  });

  function selectPreset(daysCount: number) {
    const endDate = today(getLocalTimeZone());
    draftCalendarValue = {
      start: endDate.subtract({ days: daysCount - 1 }),
      end: endDate,
    };
  }

  function handleCalendarChange(newValue: DateRange | undefined) {
    if (newValue?.start && newValue?.end) {
      draftCalendarValue = newValue;
    }
  }

  function resetFilters() {
    selectPreset(7);
  }

  function applyFilters() {
    const start = draftCalendarValue?.start;
    const end = draftCalendarValue?.end;

    if (start && end) {
      // A range matching a "last N days" preset commits as a relative range so it
      // stays anchored to today; anything else commits as an explicit custom range.
      if (selectedDays !== undefined) {
        params.setDayRange(selectedDays);
      } else {
        params.setCustomRange(start.toString(), end.toString());
      }
    }

    // Close the sidebar
    open = false;
    onOpenChange?.(false);
  }

  // Get formatted date range for display
  const dateRangeText = $derived.by(() => {
    if (draftCalendarValue?.start && draftCalendarValue?.end) {
      const start = draftCalendarValue.start.toDate(getLocalTimeZone());
      const end = draftCalendarValue.end.toDate(getLocalTimeZone());
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
                onclick={() => selectPreset(preset.days)}
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
            {dateRangeText}
          </div>
          <div class="border border-border rounded-lg overflow-hidden">
            <RangeCalendar
              bind:value={draftCalendarValue}
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
        <Button class="flex-1" onclick={applyFilters}>Apply Filters</Button>
      </div>
    </div>
  </Sheet.Content>
</Sheet.Root>
