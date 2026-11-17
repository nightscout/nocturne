<script lang="ts">
  import { formatNumber } from "#lib/utils/formatting.js";
  import { onMount } from "svelte";
  import WidgetCard from "./WidgetCard.svelte";
  import WidgetStatSkeleton from "./WidgetStatSkeleton.svelte";
  import TIRStackedChart from "#lib/components/reports/TIRStackedChart.svelte";
  import { getMultiPeriodStatistics } from "#lib/api/generated/statistics.generated.remote.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import ReliabilityBadge from "#lib/components/reports/ReliabilityBadge.svelte";
  import { remoteErrorMessage } from "#lib/api/remote-error.js";

  // Toggle between today and 90-day average
  let showAverage = $state(false);

  // Fetch on the client after hydration — the underlying API can be slow and
  // would otherwise block SSR (timing out and breaking hydration).
  let statsPromise = $state<ReturnType<typeof getMultiPeriodStatistics> | null>(
    null
  );
  onMount(() => {
    statsPromise = getMultiPeriodStatistics();
  });

  function toggleView() {
    showAverage = !showAverage;
  }
</script>

<WidgetCard title="Time in range">
  {#snippet subtitleSnippet()}
    <Button
      variant="ghost-muted"
      size="xs"
      class="-mr-2"
      onclick={toggleView}
    >
      {showAverage ? "90-Day Avg" : "Last 24h"}
    </Button>
  {/snippet}

  {#if !statsPromise}
    <WidgetStatSkeleton shape="tir" />
  {:else}
    {#await statsPromise}
      <WidgetStatSkeleton shape="tir" />
    {:then stats}
    {@const tirToday = stats?.lastDay?.analytics?.timeInRange}
    {@const tir90 = stats?.last90Days?.analytics?.timeInRange}

    <!-- Select which data to display based on toggle -->
    {@const tirMetrics = showAverage ? tir90 : tirToday}
    {@const percentages = tirMetrics?.percentages}
    {@const hasTirData = percentages && (percentages.target ?? 0) > 0}

    {#if hasTirData}
      {@const inRange = percentages?.target ?? 0}
      {@const veryLow = percentages?.veryLow ?? 0}
      {@const low = percentages?.low ?? 0}
      {@const high = percentages?.high ?? 0}
      {@const veryHigh = percentages?.veryHigh ?? 0}

      <!-- Comparison data: compare today vs 90-day, or 90-day vs itself (no comparison) -->
      {@const comparisonTir = showAverage ? null : (tir90?.percentages?.target ?? null)}
      {@const improvement = comparisonTir !== null ? inRange - comparisonTir : null}

      {@const totalReadings = showAverage
        ? (stats?.last90Days?.entryCount ?? 0)
        : (stats?.lastDay?.entryCount ?? 0)}

      <!-- Sized by WidgetGrid's @container: vertical while the widgets stack, a bar once they
           share a row. -->
      <div class="mb-2 h-32 @2xl:hidden">
        <TIRStackedChart {percentages} orientation="vertical" showLabels compact />
      </div>
      <div class="mb-2 hidden h-6 @2xl:block">
        <TIRStackedChart {percentages} orientation="horizontal" showLabels={false} compact />
      </div>

      <div class="flex items-baseline justify-between gap-2">
        <div class="flex items-baseline gap-2">
          <span class="text-xl font-semibold tabular-nums">
            {inRange.toFixed(0)}%
          </span>
          <span class="text-xs text-muted-foreground">in range</span>
          {#if improvement !== null && Math.abs(improvement) >= 0.5}
            <span class="text-xs text-muted-foreground tabular-nums">
              {improvement > 0 ? "+" : ""}{improvement.toFixed(1)}% vs 90d
            </span>
          {/if}
        </div>
        <span class="text-xs text-muted-foreground tabular-nums">
          {formatNumber(totalReadings)} readings
        </span>
      </div>

      <div class="flex justify-between text-xs text-muted-foreground mt-1 tabular-nums">
        <span>Below {(veryLow + low).toFixed(0)}%</span>
        <span>Above {(high + veryHigh).toFixed(0)}%</span>
      </div>
      {@const period = showAverage ? stats?.last90Days : stats?.lastDay}
      <ReliabilityBadge
        compact
        reliability={period?.reliability}
        timeZoneUnavailableReason={period?.analytics?.timeZoneUnavailableReason}
      />
    {:else}
      <div class="flex flex-col items-center justify-center text-muted-foreground py-4">
        <p class="text-xs">{showAverage ? "No 90-day data available" : "No data available"}</p>
      </div>
    {/if}
    {:catch err}
      <div class="flex flex-col items-center justify-center text-muted-foreground py-4">
        <p class="text-xs">Failed to load data</p>
        <p class="text-xs text-destructive">{remoteErrorMessage(err, "Try again.")}</p>
      </div>
    {/await}
  {/if}
</WidgetCard>
