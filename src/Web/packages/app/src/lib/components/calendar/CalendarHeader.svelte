<script lang="ts">
  import { Calendar, ChevronLeft, ChevronRight } from "lucide-svelte";
  import { Button } from "$lib/components/ui/button";
  import * as ToggleGroup from "$lib/components/ui/toggle-group";

  type ViewMode = "tir" | "profile";

  let {
    viewDate,
    viewMode = $bindable(),
    isCurrentMonth,
    MONTH_NAMES,
    previousMonth,
    nextMonth,
    goToToday,
    setViewMode,
  } = $props<{
    viewDate: Date;
    viewMode: ViewMode;
    isCurrentMonth: boolean;
    MONTH_NAMES: string[];
    previousMonth: () => void;
    nextMonth: () => void;
    goToToday: () => void;
    setViewMode: (mode: ViewMode) => void;
  }>();

  const currentMonth = $derived(viewDate.getMonth());
  const currentYear = $derived(viewDate.getFullYear());
</script>

<div
  class="@container border-b bg-background/95 backdrop-blur supports-backdrop-filter:bg-background/60 sticky top-0 z-10"
>
  <div class="flex flex-wrap items-center justify-between gap-2 p-4">
    <div class="flex items-center gap-4">
      <Calendar class="h-6 w-6 text-muted-foreground" />
      <h1 class="text-2xl font-bold">Calendar</h1>
    </div>

    <!-- Navigation Controls -->
    <div class="flex items-center gap-2">
      <Button variant="outline" size="icon" onclick={previousMonth}>
        <ChevronLeft class="h-4 w-4" />
      </Button>
      <div class="text-lg font-semibold min-w-[180px] text-center">
        {MONTH_NAMES[currentMonth]}
        {currentYear}
      </div>
      <Button variant="outline" size="icon" onclick={nextMonth}>
        <ChevronRight class="h-4 w-4" />
      </Button>
      {#if !isCurrentMonth}
        <Button variant="outline" size="sm" onclick={goToToday}>
          Today
        </Button>
      {/if}
    </div>
  </div>

  <!-- Legend and View Toggle -->
  <div class="flex flex-wrap items-center justify-between gap-x-4 gap-y-2 px-4 pb-3">
    <div class="flex flex-wrap items-center gap-3 @xl:gap-6">
      <!-- View Mode Toggle -->
      <ToggleGroup.Root
        type="single"
        value={viewMode}
        onValueChange={(value: string) =>
          value && setViewMode(value as ViewMode)}
        class="border rounded-md"
      >
        <ToggleGroup.Item value="tir" class="text-xs px-3">
          TIR
        </ToggleGroup.Item>
        <ToggleGroup.Item value="profile" class="text-xs px-3">
          Profile
        </ToggleGroup.Item>
      </ToggleGroup.Root>

      <!-- Glucose colors -->
      <div class="flex items-center gap-3">
        <div class="flex items-center gap-1.5">
          <div class="h-3 w-3 rounded bg-glucose-in-range"></div>
          <span class="text-sm">In Range</span>
        </div>
        <div class="flex items-center gap-1.5">
          <div class="h-3 w-3 rounded bg-glucose-low"></div>
          <span class="text-sm">Low</span>
        </div>
        <div class="flex items-center gap-1.5">
          <div class="h-3 w-3 rounded bg-glucose-high"></div>
          <span class="text-sm">High</span>
        </div>
      </div>

      <!-- Mode-specific explanation -->
      {#if viewMode === "profile"}
        <div
          class="hidden @2xl:flex items-center gap-3 text-xs text-muted-foreground border-l pl-4"
        >
          <span>Daily glucose profile</span>
          <span>·</span>
          <span>Green band = target range</span>
        </div>
      {/if}
    </div>
    <div class="hidden @xl:block text-sm text-muted-foreground">
      Click any day to view detailed report
    </div>
  </div>
</div>
