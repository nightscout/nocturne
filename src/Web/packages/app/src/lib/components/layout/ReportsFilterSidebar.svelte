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
  import RangeCalendar from "$lib/components/ui/range-calendar/range-calendar.svelte";
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

  // === DRAFT STATE ===
  // These represent the user's pending selections before clicking "Apply Filters"
  let draftDays = $state<number | undefined>(undefined);
  let draftCalendarValue = $state<DateRange | undefined>(undefined);

  // Track whether user selected a preset or used the calendar
  let draftMode = $state<"preset" | "calendar">("preset");

  // Initialize draft state when sidebar opens
  $effect(() => {
    if (open) {
      // Reset draft to current params when opening
      draftDays = params.days ?? undefined;
      draftMode = params.days ? "preset" : "calendar";

      if (params.from && params.to) {
        try {
          const startDate = parseDate(params.from);
          const endDate = parseDate(params.to);
          draftCalendarValue = { start: startDate, end: endDate };
        } catch {
          // Fall through to days-based calculation
          if (params.days) {
            const endDate = today(getLocalTimeZone());
            const startDate = endDate.subtract({ days: params.days - 1 });
            draftCalendarValue = { start: startDate, end: endDate };
          }
        }
      } else if (params.days) {
        const endDate = today(getLocalTimeZone());
        const startDate = endDate.subtract({ days: params.days - 1 });
        draftCalendarValue = { start: startDate, end: endDate };
      }
    }
  });

  // Derived state for selected days (for UI highlighting in draft mode)
  const selectedDays = $derived(draftMode === "preset" ? draftDays : undefined);

  function selectPreset(daysCount: number) {
    draftDays = daysCount;
    draftMode = "preset";

    // Also update calendar to show the preset range
    const endDate = today(getLocalTimeZone());
    const startDate = endDate.subtract({ days: daysCount - 1 });
    draftCalendarValue = { start: startDate, end: endDate };
  }

  function handleCalendarChange(newValue: DateRange | undefined) {
    if (newValue?.start && newValue?.end) {
      draftCalendarValue = newValue;
      draftMode = "calendar";
      draftDays = undefined; // Clear preset selection
    }
  }

  function resetFilters() {
    // Reset draft to default 7 days
    draftDays = 7;
    draftMode = "preset";

    const endDate = today(getLocalTimeZone());
    const startDate = endDate.subtract({ days: 6 });
    draftCalendarValue = { start: startDate, end: endDate };
  }

  function applyFilters() {
    // Commit draft state to URL params
    if (draftMode === "preset" && draftDays) {
      params.setDayRange(draftDays);
    } else if (
      draftMode === "calendar" &&
      draftCalendarValue?.start &&
      draftCalendarValue?.end
    ) {
      params.setCustomRange(
        draftCalendarValue.start.toString(),
        draftCalendarValue.end.toString()
      );
    }

    // Close the sidebar
    open = false;
    onOpenChange?.(false);
  }

  // Get formatted date range for display
  const dateRangeText = $derived(() => {
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
            {dateRangeText()}
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
