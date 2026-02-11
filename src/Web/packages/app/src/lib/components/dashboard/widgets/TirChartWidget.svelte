<script lang="ts">
  import WidgetCard from "./WidgetCard.svelte";
  import TIRStackedChart from "$lib/components/reports/TIRStackedChart.svelte";
  import { TrendingUp, TrendingDown, Minus } from "lucide-svelte";
  import { getMultiPeriodStatistics } from "$lib/data/generated";
  import { MediaQuery } from "svelte/reactivity";
  import { Button } from "$lib/components/ui/button";

  // Toggle between today and 90-day average
  let showAverage = $state(false);

  // Fetch statistics from backend (includes 1-day, 90-day data)
  const statsPromise = $derived(getMultiPeriodStatistics());

  // Responsive breakpoint - use horizontal for larger screens (>= 640px / sm breakpoint)
  const isLargeScreen = new MediaQuery("(min-width: 640px)");

  function toggleView() {
    showAverage = !showAverage;
  }
</script>

<WidgetCard title="Time in Range">
  {#snippet subtitleSnippet()}
    <Button
      variant="ghost"
      size="sm"
      class="h-5 px-1.5 text-xs text-muted-foreground hover:text-foreground -ml-1.5"
      onclick={toggleView}
    >
      {showAverage ? "90-Day Avg" : "Last 24h"}
    </Button>
  {/snippet}

  {#await statsPromise}
    <div class="flex items-center justify-center py-4">
      <div class="h-4 w-4 animate-spin rounded-full border-2 border-primary border-t-transparent"></div>
    </div>
  {:then stats}
    {@const tirToday = stats?.lastDay?.analytics?.timeInRange}
    {@const tir90 = stats?.last90Days?.analytics?.timeInRange}

    <!-- Select which data to display based on toggle -->
    {@const tirMetrics = showAverage ? tir90 : tirToday}
    {@const percentages = tirMetrics?.percentages}
    {@const hasTirData = percentages && (percentages.target ?? 0) > 0}

    {#if hasTirData}
      {@const inRange = percentages?.target ?? 0}
      {@const severeLow = percentages?.severeLow ?? 0}
      {@const low = percentages?.low ?? 0}
      {@const high = percentages?.high ?? 0}
      {@const severeHigh = percentages?.severeHigh ?? 0}

      <!-- Comparison data: compare today vs 90-day, or 90-day vs itself (no comparison) -->
      {@const comparisonTir = showAverage ? null : (tir90?.percentages?.target ?? null)}
      {@const improvement = comparisonTir !== null ? inRange - comparisonTir : null}

      {@const totalReadings = showAverage
        ? (stats?.last90Days?.entryCount ?? 0)
        : (stats?.lastDay?.entryCount ?? 0)}

      <!-- Stacked bar chart - horizontal on larger screens, vertical on mobile -->
      <div class={isLargeScreen.current ? "h-6 mb-2" : "h-32 mb-2"}>
        <TIRStackedChart
          {percentages}
          orientation={isLargeScreen.current ? "horizontal" : "vertical"}
          showLabels={!isLargeScreen.current}
          showThresholds={false}
          compact
        />
      </div>

      <!-- TIR percentage with improvement indicator -->
      <div class="flex items-center justify-between">
        <div class="flex items-baseline gap-2">
          <span class="text-2xl font-bold" style="color: var(--glucose-in-range);">
            {inRange.toFixed(0)}%
          </span>
          {#if improvement !== null}
            {@const absImprovement = Math.abs(improvement)}
            {#if absImprovement >= 0.5}
              <span
                class="inline-flex items-center gap-0.5 text-xs font-medium {improvement > 0
                  ? 'text-green-500'
                  : 'text-red-500'}"
              >
                {#if improvement > 0}
                  <TrendingUp class="h-3 w-3" />
                  +{improvement.toFixed(1)}%
                {:else}
                  <TrendingDown class="h-3 w-3" />
                  {improvement.toFixed(1)}%
                {/if}
              </span>
            {:else}
              <span class="inline-flex items-center gap-0.5 text-xs font-medium text-muted-foreground">
                <Minus class="h-3 w-3" />
                vs 90d
              </span>
            {/if}
          {/if}
        </div>
        <span class="text-xs text-muted-foreground">
          {totalReadings.toLocaleString()} readings
        </span>
      </div>

      <!-- Low/High summary -->
      <div class="flex justify-between text-xs text-muted-foreground mt-1">
        <span style="color: var(--glucose-low);">
          ↓ {(severeLow + low).toFixed(0)}%
        </span>
        <span style="color: var(--glucose-high);">
          ↑ {(high + severeHigh).toFixed(0)}%
        </span>
      </div>
    {:else}
      <div class="flex flex-col items-center justify-center text-muted-foreground py-4">
        <p class="text-xs">{showAverage ? "No 90-day data available" : "No data available"}</p>
      </div>
    {/if}
  {:catch err}
    <div class="flex flex-col items-center justify-center text-muted-foreground py-4">
      <p class="text-xs">Failed to load data</p>
      <p class="text-xs text-destructive">{err?.message ?? JSON.stringify(err)}</p>
    </div>
  {/await}
</WidgetCard>
