<script lang="ts">
  import WidgetCard from "./WidgetCard.svelte";
  import { Text, PieChart } from "layerchart";
  import { getMultiPeriodStatistics } from "$lib/data/generated";
  import { Button } from "$lib/components/ui/button";

  // Toggle between today and 90-day average
  let showAverage = $state(false);

  // Fetch TDD data from backend statistics
  const statsPromise = $derived(getMultiPeriodStatistics());

  function toggleView() {
    showAverage = !showAverage;
  }
</script>

<WidgetCard title="Total Daily Dose">
  {#snippet subtitleSnippet()}
    <Button
      variant="ghost"
      size="sm"
      class="h-5 px-1.5 text-xs text-muted-foreground hover:text-foreground -ml-1.5"
      onclick={toggleView}
    >
      {showAverage ? "90-Day Avg" : "Today"}
    </Button>
  {/snippet}

  {#await statsPromise}
    <div class="flex items-center justify-center py-4">
      <div class="h-4 w-4 animate-spin rounded-full border-2 border-primary border-t-transparent"></div>
    </div>
  {:then stats}
    {@const todaySummary = stats?.lastDay?.treatmentSummary}
    {@const ninetyDaySummary = stats?.last90Days?.treatmentSummary}
    {@const periodDays = stats?.last90Days?.periodDays ?? 90}

    <!-- Calculate today's values -->
    {@const todayBolus = todaySummary?.totals?.insulin?.bolus ?? 0}
    {@const todayBasal = todaySummary?.totals?.insulin?.basal ?? 0}
    {@const todayTotal = todayBolus + todayBasal}
    {@const todayCarbs = todaySummary?.totals?.food?.carbs ?? 0}

    <!-- Calculate 90-day average values -->
    {@const avgBolus = periodDays > 0 ? (ninetyDaySummary?.totals?.insulin?.bolus ?? 0) / periodDays : 0}
    {@const avgBasal = periodDays > 0 ? (ninetyDaySummary?.totals?.insulin?.basal ?? 0) / periodDays : 0}
    {@const avgTotal = avgBolus + avgBasal}
    {@const avgCarbs = periodDays > 0 ? (ninetyDaySummary?.totals?.food?.carbs ?? 0) / periodDays : 0}

    <!-- Select which values to display based on toggle -->
    {@const bolus = showAverage ? avgBolus : todayBolus}
    {@const basal = showAverage ? avgBasal : todayBasal}
    {@const total = showAverage ? avgTotal : todayTotal}
    {@const carbs = showAverage ? avgCarbs : todayCarbs}

    {#if total > 0 || carbs > 0}
      {@const segmentData = [
        { key: "Bolus", value: bolus, color: "var(--iob-bolus)" },
        { key: "Basal", value: basal, color: "var(--iob-basal)" },
      ].filter(s => s.value > 0)}

      <div class="flex items-center justify-center">
        <div class="h-[100px] w-[100px]">
          {#if total > 0}
            <PieChart
              data={segmentData}
              key="key"
              value="value"
              cRange={["var(--iob-bolus)", "var(--iob-basal)"]}
              innerRadius={-20}
              cornerRadius={2}
              padAngle={0.02}
              renderContext={"svg"}
            >
              {#snippet aboveMarks()}
                <Text
                  value={`${total.toFixed(1)}U`}
                  textAnchor="middle"
                  verticalAnchor="middle"
                  class="fill-foreground font-bold text-lg tabular-nums"
                />
              {/snippet}
            </PieChart>
          {:else}
            <div class="flex items-center justify-center h-full text-muted-foreground text-sm">
              No insulin
            </div>
          {/if}
        </div>
      </div>

      <!-- Legend -->
      <div class="flex justify-between text-xs mt-2">
        <span class="flex items-center gap-1.5">
          <span
            class="w-2 h-2 rounded-full"
            style="background-color: var(--iob-bolus);"
          ></span>
          Bolus {bolus.toFixed(1)}U
        </span>
        <span class="flex items-center gap-1.5">
          <span
            class="w-2 h-2 rounded-full"
            style="background-color: var(--iob-basal);"
          ></span>
          Basal {basal.toFixed(1)}U
        </span>
      </div>

      <!-- Carbs -->
      <div class="flex justify-center text-xs mt-2 pt-2 border-t border-border">
        <span class="flex items-center gap-1.5 text-muted-foreground">
          <span
            class="w-2 h-2 rounded-full"
            style="background-color: var(--cob-carbs, hsl(var(--chart-3)));"
          ></span>
          Carbs {carbs.toFixed(0)}g
        </span>
      </div>
    {:else}
      <div class="flex flex-col items-center justify-center text-muted-foreground py-4">
        <p class="text-xs">{showAverage ? "No data for 90-day average" : "No data today"}</p>
      </div>
    {/if}
  {:catch err}
    <div class="flex flex-col items-center justify-center text-muted-foreground py-4">
      <p class="text-xs">Failed to load data</p>
      <p class="text-xs text-destructive">{err?.message ?? JSON.stringify(err)}</p>
    </div>
  {/await}
</WidgetCard>
