<script lang="ts">
  import { cn } from "$lib/utils";
  import * as Tooltip from "$lib/components/ui/tooltip";
  import * as Popover from "$lib/components/ui/popover";
  import { TrackerCategoryIcon } from "$lib/components/icons";
  import DayStackedBar from "$lib/components/calendar/DayStackedBar.svelte";
  import DayGlucoseProfile from "$lib/components/calendar/DayGlucoseProfile.svelte";
  import TrackerPopoverContent from "$lib/components/calendar/TrackerPopoverContent.svelte";
  import { TrackerCategory } from "$api";
  import type { TrackerInstanceDto, TrackerDefinitionDto } from "$api";
  import { formatGlucoseValue } from "$lib/utils/formatting";
  import type { GlucoseUnits } from "$lib/utils/formatting";
  import { formatCalendarDate, getCalendarDayNumber } from "$lib/components/calendar/calendar-date";

  let {
    day,
    viewMode,
    currentYear,
    currentMonth,
    trackerEvents,
    definitions,
    openPopoverId = $bindable(),
    units,
    unitLabel,
    handleDayClick,
    getDefinition,
    getTrackerLevel,
    getTrackerIconColor,
    formatTrackerStartTime,
    formatTrackerAge,
    openCompletionDialog,
  } = $props<{
    day: any; // Using any for brevity in this complex propset, but it maps to calendar logic
    viewMode: "tir" | "profile";
    currentYear: number;
    currentMonth: number;
    trackerEvents: Map<string, any[]>;
    definitions: TrackerDefinitionDto[];
    openPopoverId: string | null;
    units: GlucoseUnits;
    unitLabel: string;
    handleDayClick: (day: any) => void;
    getDefinition: (instance: TrackerInstanceDto, defs: TrackerDefinitionDto[]) => TrackerDefinitionDto | undefined;
    getTrackerLevel: (instance: TrackerInstanceDto, def: TrackerDefinitionDto | undefined) => string;
    getTrackerIconColor: (eventType: string, level: string) => string;
    formatTrackerStartTime: (startedAt: Date | undefined) => string | null;
    formatTrackerAge: (hours: number | undefined) => string;
    openCompletionDialog: (instance: TrackerInstanceDto, def: TrackerDefinitionDto | undefined, date: string) => void;
  }>();

  // Helper for today check (can be simplified if passed as prop)
  function isToday(date: string): boolean {
    const now = new Date();
    const todayStr = `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, "0")}-${String(now.getDate()).padStart(2, "0")}`;
    return date === todayStr;
  }

  function getCellClasses(
    day: any
  ): string {
    const base = "flex items-center justify-center rounded-lg border min-h-20 relative";
    const isTodayCell = day && "date" in day && isToday(day.date);

    return cn(
      base,
      isTodayCell
        ? "bg-primary/5 border-primary"
        : "border-border/50 bg-background/50"
    );
  }

  const dateStr = $derived(
    day && "date" in day
      ? day.date
      : day && "dayNumber" in day
        ? `${currentYear}-${String(currentMonth + 1).padStart(2, "0")}-${String(day.dayNumber).padStart(2, "0")}`
        : null
  );

  const dayTrackerEvents = $derived(dateStr ? (trackerEvents.get(dateStr) ?? []) : []);
</script>

<div class={getCellClasses(day)}>
  {#if day && "date" in day && day.totalReadings > 0}
    <!-- Day number in corner -->
    <span
      class="absolute top-1 left-2 text-xs text-muted-foreground font-medium z-10"
    >
      {getCalendarDayNumber(day.date)}
    </span>

    <!-- Tracker icons in top-right corner -->
    {#if dayTrackerEvents.length > 0}
      <div class="absolute top-1 right-1 flex gap-0.5 z-10">
        {#each dayTrackerEvents as event}
          {@const def = getDefinition(event.instance, definitions)}
          {@const level = event.eventType === "due" ? getTrackerLevel(event.instance, def) : "none"}
          {@const category = def?.category ?? TrackerCategory.Consumable}
          {@const startTime = formatTrackerStartTime(event.instance.startedAt)}
          {@const popoverId = `${event.instance.id}-${event.date}`}
          <Popover.Root
            open={openPopoverId === popoverId}
            onOpenChange={(open) => (openPopoverId = open ? popoverId : null)}
          >
            <Popover.Trigger>
              {#snippet child({ props }: { props: Record<string, unknown> })}
                <button
                  {...props}
                  class={cn(
                    "h-4 w-4 rounded-full flex items-center justify-center hover:scale-125 transition-transform",
                    getTrackerIconColor(event.eventType, level)
                  )}
                  title={event.instance.definitionName}
                >
                  <TrackerCategoryIcon {category} class="h-3 w-3" />
                </button>
              {/snippet}
            </Popover.Trigger>
            <Popover.Content class="w-64 p-3" side="top">
              <TrackerPopoverContent
                {event}
                {category}
                {startTime}
                {level}
                {formatTrackerAge}
                {openCompletionDialog}
                {def}
              />
            </Popover.Content>
          </Popover.Root>
        {/each}
      </div>
    {/if}

    <!-- Show chart based on view mode -->
    <Tooltip.Root>
      <Tooltip.Trigger>
        {#snippet child({ props }: { props: Record<string, unknown> })}
          {#if viewMode === "tir"}
            <div
              {...props}
              class="absolute inset-0 p-2 pt-6"
            >
              <DayStackedBar
                lowPercent={day.lowPercent}
                inRangePercent={day.inRangePercent}
                highPercent={day.highPercent}
                onclick={() => handleDayClick(day)}
              />
            </div>
          {:else}
            <div {...props} class="absolute inset-0">
              <DayGlucoseProfile
                entries={day.entries}
                dayStartMills={day.timestamp}
                onclick={() => handleDayClick(day)}
              />
            </div>
          {/if}
        {/snippet}
      </Tooltip.Trigger>
      <Tooltip.Content
        side="top"
        class="bg-card text-card-foreground border shadow-lg p-3"
      >
        <div class="space-y-1.5">
          <div class="font-medium text-sm">
            {formatCalendarDate(day.date, undefined, {
              weekday: "long",
              month: "short",
              day: "numeric",
            })}
          </div>
          <div class="grid grid-cols-2 gap-x-4 gap-y-1 text-xs">
            <div class="flex items-center gap-1.5">
              <span class="w-2 h-2 rounded-full bg-glucose-in-range"></span>
              <span class="text-muted-foreground">In Range:</span>
            </div>
            <span class="font-medium">{day.inRangePercent.toFixed(1)}%</span>
            <div class="flex items-center gap-1.5">
              <span class="w-2 h-2 rounded-full bg-glucose-low"></span>
              <span class="text-muted-foreground">Low:</span>
            </div>
            <span class="font-medium">{day.lowPercent.toFixed(1)}%</span>
            <div class="flex items-center gap-1.5">
              <span class="w-2 h-2 rounded-full bg-glucose-high"></span>
              <span class="text-muted-foreground">High:</span>
            </div>
            <span class="font-medium">{day.highPercent.toFixed(1)}%</span>
          </div>
          <div class="border-t pt-1.5 mt-1.5 grid grid-cols-2 gap-x-4 gap-y-1 text-xs">
            <span class="text-muted-foreground">Carbs:</span>
            <span class="font-medium">{day.totalCarbs.toFixed(0)}g</span>
            <span class="text-muted-foreground">Bolus:</span>
            <span class="font-medium">{day.totalBolus.toFixed(1)}U</span>
            <span class="text-muted-foreground">Basal:</span>
            <span class="font-medium">{day.totalBasal.toFixed(1)}U</span>
            <span class="text-muted-foreground">Avg Glucose:</span>
            <span class="font-medium">
              {formatGlucoseValue(day.averageGlucose, units)} {unitLabel}
            </span>
          </div>
          <div class="text-xs text-muted-foreground italic pt-1">
            Click to view full day report
          </div>
        </div>
      </Tooltip.Content>
    </Tooltip.Root>
  {:else if day && "empty" in day}
    <!-- Day with no data -->
    <span class="absolute top-1 left-2 text-xs text-muted-foreground">
      {day.dayNumber}
    </span>
    <!-- Tracker icons for empty days -->
    {#if dayTrackerEvents.length > 0}
      <div class="absolute top-1 right-1 flex gap-0.5">
        {#each dayTrackerEvents as event}
          {@const def = getDefinition(event.instance, definitions)}
          {@const level = event.eventType === "due" ? getTrackerLevel(event.instance, def) : "none"}
          {@const category = def?.category ?? TrackerCategory.Consumable}
          {@const startTime = formatTrackerStartTime(event.instance.startedAt)}
          {@const popoverId = `${event.instance.id}-${event.date}`}
          <Popover.Root
            open={openPopoverId === popoverId}
            onOpenChange={(open) => (openPopoverId = open ? popoverId : null)}
          >
            <Popover.Trigger>
              {#snippet child({ props }: { props: Record<string, unknown> })}
                <button
                  {...props}
                  class={cn(
                    "h-4 w-4 rounded-full flex items-center justify-center hover:scale-125 transition-transform",
                    getTrackerIconColor(event.eventType, level)
                  )}
                  title={event.instance.definitionName}
                >
                  <TrackerCategoryIcon {category} class="h-3 w-3" />
                </button>
              {/snippet}
            </Popover.Trigger>
            <Popover.Content class="w-64 p-3" side="top">
              <TrackerPopoverContent
                {event}
                {category}
                {startTime}
                {level}
                {formatTrackerAge}
                {openCompletionDialog}
                {def}
              />
            </Popover.Content>
          </Popover.Root>
        {/each}
      </div>
    {/if}
    <div class="w-6 h-6 rounded-full border-2 border-dashed border-muted-foreground/20"></div>
  {:else if day && "date" in day}
    <!-- Day exists in data but has no readings -->
    <span class="absolute top-1 left-2 text-xs text-muted-foreground">
      {getCalendarDayNumber(day.date)}
    </span>
    <!-- Tracker icons for days with no readings -->
    {#if dayTrackerEvents.length > 0}
      <div class="absolute top-1 right-1 flex gap-0.5">
        {#each dayTrackerEvents as event}
          {@const def = getDefinition(event.instance, definitions)}
          {@const level = event.eventType === "due" ? getTrackerLevel(event.instance, def) : "none"}
          {@const category = def?.category ?? TrackerCategory.Consumable}
          {@const startTime = formatTrackerStartTime(event.instance.startedAt)}
          {@const popoverId = `${event.instance.id}-${event.date}`}
          <Popover.Root
            open={openPopoverId === popoverId}
            onOpenChange={(open) => (openPopoverId = open ? popoverId : null)}
          >
            <Popover.Trigger>
              {#snippet child({ props }: { props: Record<string, unknown> })}
                <button
                  {...props}
                  class={cn(
                    "h-4 w-4 rounded-full flex items-center justify-center hover:scale-125 transition-transform",
                    getTrackerIconColor(event.eventType, level)
                  )}
                  title={event.instance.definitionName}
                >
                  <TrackerCategoryIcon {category} class="h-3 w-3" />
                </button>
              {/snippet}
            </Popover.Trigger>
            <Popover.Content class="w-64 p-3" side="top">
              <TrackerPopoverContent
                {event}
                {category}
                {startTime}
                {level}
                {formatTrackerAge}
                {openCompletionDialog}
                {def}
              />
            </Popover.Content>
          </Popover.Root>
        {/each}
      </div>
    {/if}
    <div class="w-6 h-6 rounded-full border-2 border-dashed border-muted-foreground/20"></div>
  {:else}
    <!-- Empty cell (before/after month) -->
  {/if}
</div>
