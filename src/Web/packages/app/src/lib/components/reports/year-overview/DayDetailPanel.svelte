<script lang="ts">
  import { X, ArrowRight } from "lucide-svelte";
  import { Button } from "$lib/components/ui/button";
  import { Separator } from "$lib/components/ui/separator";
  import { slide } from "svelte/transition";
  import { cubicOut } from "svelte/easing";
  import { formatGlucoseValue } from "$lib/utils/formatting";
  import type { GlucoseUnits } from "$lib/utils/formatting";
  import { getDataTypeLabel } from "$lib/utils/data-type-labels";

  let {
    selectedDay,
    units,
    unitLabel,
    formatSelectedDate,
    formatUnits,
    glucoseColorScale,
    getVisibleCounts,
    closeDetailPanel,
    navigateToDayInReview,
  } = $props<{
    selectedDay: any; // Using any for brevity in this refactor, but it's CalendarDatum
    units: GlucoseUnits;
    unitLabel: string;
    formatSelectedDate: (dateStr: string) => string;
    formatUnits: (value: number | null) => string;
    glucoseColorScale: any;
    getVisibleCounts: (counts: Record<string, number>) => [string, number][];
    closeDetailPanel: () => void;
    navigateToDayInReview: (dateStr: string) => void;
  }>();
</script>

{#if selectedDay}
  <div
    class="fixed right-0 top-14 z-30 flex h-[calc(100vh-3.5rem)] w-80 flex-col border-l border-border bg-card shadow-lg lg:w-96"
    transition:slide={{ axis: "x", duration: 200, easing: cubicOut }}
  >
    <!-- Panel Header -->
    <div
      class="flex items-center justify-between border-b border-border px-4 py-3"
    >
      <h3 class="text-sm font-semibold">Day Details</h3>
      <button onclick={closeDetailPanel} class="p-2 hover:bg-muted rounded-md transition-colors">
        <X class="h-4 w-4" />
      </button>
    </div>

    <!-- Panel Content -->
    <div class="flex-1 overflow-y-auto px-4 py-4">
      <!-- Date -->
      <div class="mb-4">
        <h4 class="text-lg font-semibold">
          {formatSelectedDate(selectedDay.dateString)}
        </h4>
      </div>

      <Separator class="mb-4" />

      <!-- Average Glucose -->
      {#if selectedDay.averageGlucoseMgdl != null}
        <div class="mb-4">
          <div
            class="text-xs font-medium uppercase tracking-wide text-muted-foreground"
          >
            Average Glucose
          </div>
          <div class="mt-1 text-2xl font-bold tabular-nums">
            <span
              style="color: {glucoseColorScale(
                selectedDay.averageGlucoseMgdl
              )}"
            >
              {formatGlucoseValue(selectedDay.averageGlucoseMgdl, units)}
            </span>
            <span class="text-sm font-normal text-muted-foreground">
              {unitLabel}
            </span>
          </div>
        </div>
        <Separator class="mb-4" />
      {/if}

      <!-- Insulin & Carbs Summary -->
      {#if selectedDay.totalDailyDose != null || selectedDay.totalCarbs != null}
        <div class="mb-4">
          {#if selectedDay.totalDailyDose != null}
            <div
              class="mb-2 text-xs font-medium uppercase tracking-wide text-muted-foreground"
            >
              Insulin
            </div>
            <div class="space-y-2">
              <div
                class="flex items-center justify-between rounded-md bg-muted/50 px-3 py-2 text-sm"
              >
                <span>Bolus</span>
                <span class="font-medium tabular-nums">
                  {formatUnits(selectedDay.totalBolusUnits)}
                </span>
              </div>
              <div
                class="flex items-center justify-between rounded-md bg-muted/50 px-3 py-2 text-sm"
              >
                <span>Basal</span>
                <span class="font-medium tabular-nums">
                  {formatUnits(selectedDay.totalBasalUnits)}
                </span>
              </div>
              <div
                class="flex items-center justify-between rounded-md bg-primary/10 px-3 py-2 text-sm font-medium"
              >
                <span>Total Daily Dose</span>
                <span class="tabular-nums">
                  {formatUnits(selectedDay.totalDailyDose)}
                </span>
              </div>
            </div>
          {/if}
          {#if selectedDay.totalCarbs != null}
            <div
              class="mb-2 text-xs font-medium uppercase tracking-wide text-muted-foreground {selectedDay.totalDailyDose !=
              null
                ? 'mt-4'
                : ''}"
            >
              Carbs
            </div>
            <div
              class="flex items-center justify-between rounded-md bg-muted/50 px-3 py-2 text-sm"
            >
              <span>Total Carbs</span>
              <span class="font-medium tabular-nums">
                {selectedDay.totalCarbs.toFixed(0)}g
              </span>
            </div>
          {/if}
        </div>
        <Separator class="mb-4" />
      {/if}

      <!-- Total Count -->
      <div class="mb-4">
        <div
          class="text-xs font-medium uppercase tracking-wide text-muted-foreground"
        >
          Total Records
        </div>
        <div class="mt-1 text-xl font-bold tabular-nums">
          {selectedDay.totalCount}
        </div>
      </div>

      <!-- Per-data-type Counts -->
      {#if getVisibleCounts(selectedDay.counts).length > 0}
        {@const visiblePanelCounts = getVisibleCounts(selectedDay.counts)}
        <Separator class="mb-4" />
        <div class="mb-4">
          <div
            class="mb-2 text-xs font-medium uppercase tracking-wide text-muted-foreground"
          >
            By Data Type
          </div>
          <div class="space-y-2">
            {#each visiblePanelCounts as [key, count]}
              <div
                class="flex items-center justify-between rounded-md bg-muted/50 px-3 py-2 text-sm"
              >
                <span>{getDataTypeLabel(key)}</span>
                <span class="font-medium tabular-nums">{count}</span>
              </div>
            {/each}
          </div>
        </div>
      {/if}

      <!-- View Day in Review Button -->
      <div class="mt-6">
        <Button
          class="w-full gap-2"
          onclick={() => {
            if (selectedDay) navigateToDayInReview(selectedDay.dateString);
          }}
        >
          View Day in Review
          <ArrowRight class="h-4 w-4" />
        </Button>
      </div>
    </div>
  </div>
{/if}
