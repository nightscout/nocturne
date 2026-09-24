<script lang="ts">
  import { X, ArrowRight } from "lucide-svelte";
  import { Button } from "$lib/components/ui/button";
  import { Separator } from "$lib/components/ui/separator";
  import { slide } from "svelte/transition";
  import { cubicOut } from "svelte/easing";
  import { formatGlucoseValue } from "$lib/utils/formatting";
  import type { GlucoseUnits } from "$lib/utils/formatting";
  import { getDataTypeLabel } from "$lib/utils/data-type-labels";
  import type { YearCalendarDatum } from "./calendar-datum";


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
  }: {
    selectedDay: YearCalendarDatum | null;
    units: GlucoseUnits;
    unitLabel: string;
    formatSelectedDate: (dateStr: string) => string;
    formatUnits: (value: number | null) => string;
    glucoseColorScale: (mgdl: number) => string;
    getVisibleCounts: (counts: Record<string, number>) => [string, number][];
    closeDetailPanel: () => void;
    navigateToDayInReview: (dateStr: string) => void;
  } = $props();
</script>

{#if selectedDay}
  <div
    class="fixed right-0 top-14 z-30 flex h-[calc(100vh-3.5rem)] w-80 flex-col border-l border-border bg-card shadow-lg lg:w-96"
    transition:slide={{ axis: "x", duration: 200, easing: cubicOut }}
  >
    <div
      class="flex items-center justify-between border-b border-border px-4 py-3"
    >
      <h3 class="text-sm font-semibold">Day Details</h3>
      <Button variant="ghost" size="icon" onclick={closeDetailPanel}>
        <X class="h-4 w-4" />
      </Button>
    </div>

    <div class="flex-1 overflow-y-auto px-4 py-4">
      <div class="mb-4">
        <h4 class="text-lg font-semibold">
          {formatSelectedDate(selectedDay.dateString)}
        </h4>
      </div>

      <Separator class="mb-4" />

      {#if selectedDay.averageGlucoseMgdl != null}
        <div class="mb-4">
          <div class="text-sm font-medium text-muted-foreground">Average Glucose</div>
          <div class="mt-1 flex items-center gap-2">
            <span
              class="inline-block h-2.5 w-2.5 shrink-0 rounded-full bg-(--avg-color)"
              style:--avg-color={glucoseColorScale(selectedDay.averageGlucoseMgdl)}
              aria-hidden="true"
            ></span>
            <span class="text-2xl font-semibold tabular-nums">
              {formatGlucoseValue(selectedDay.averageGlucoseMgdl, units)}
            </span>
            <span class="text-sm text-muted-foreground">
              {unitLabel}
            </span>
          </div>
        </div>
        <Separator class="mb-4" />
      {/if}

      {#if selectedDay.totalDailyDose != null || selectedDay.totalCarbs != null}
        <div class="mb-4">
          {#if selectedDay.totalDailyDose != null}
            <div class="mb-1 text-sm font-medium text-muted-foreground">Insulin</div>
            <dl class="m-0 divide-y divide-border text-sm">
              <div class="flex items-center justify-between py-2">
                <dt>Bolus</dt>
                <dd class="m-0 font-medium tabular-nums">
                  {formatUnits(selectedDay.totalBolusUnits)}
                </dd>
              </div>
              <div class="flex items-center justify-between py-2">
                <dt>Basal</dt>
                <dd class="m-0 font-medium tabular-nums">
                  {formatUnits(selectedDay.totalBasalUnits)}
                </dd>
              </div>
              <div class="flex items-center justify-between py-2 font-medium">
                <dt>Total Daily Dose</dt>
                <dd class="m-0 font-semibold tabular-nums">
                  {formatUnits(selectedDay.totalDailyDose)}
                </dd>
              </div>
            </dl>
          {/if}
          {#if selectedDay.totalCarbs != null}
            <div
              class="mb-1 text-sm font-medium text-muted-foreground {selectedDay.totalDailyDose !=
              null
                ? 'mt-4'
                : ''}"
            >
              Carbs
            </div>
            <dl class="m-0 text-sm">
              <div class="flex items-center justify-between py-2">
                <dt>Total Carbs</dt>
                <dd class="m-0 font-medium tabular-nums">
                  {selectedDay.totalCarbs.toFixed(0)}g
                </dd>
              </div>
            </dl>
          {/if}
        </div>
        <Separator class="mb-4" />
      {/if}

      <div class="mb-4">
        <div class="text-sm font-medium text-muted-foreground">Total Records</div>
        <div class="mt-1 text-lg font-semibold tabular-nums">
          {selectedDay.totalCount}
        </div>
      </div>

      {#if getVisibleCounts(selectedDay.counts).length > 0}
        {@const visiblePanelCounts = getVisibleCounts(selectedDay.counts)}
        <Separator class="mb-4" />
        <div class="mb-4">
          <div class="mb-1 text-sm font-medium text-muted-foreground">By Data Type</div>
          <dl class="m-0 divide-y divide-border text-sm">
            {#each visiblePanelCounts as [key, count] (key)}
              <div class="flex items-center justify-between py-2">
                <dt>{getDataTypeLabel(key)}</dt>
                <dd class="m-0 font-medium tabular-nums">{count}</dd>
              </div>
            {/each}
          </dl>
        </div>
      {/if}

      <div class="mt-6">
        <Button
          class="w-full"
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
