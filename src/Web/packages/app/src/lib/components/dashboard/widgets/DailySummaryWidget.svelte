<script lang="ts">
  import { onMount } from "svelte";
  import WidgetCard from "./WidgetCard.svelte";
  import WidgetStatSkeleton from "./WidgetStatSkeleton.svelte";
  import { getMultiPeriodStatistics } from "$api/generated/statistics.generated.remote";
  import { remoteErrorMessage } from "$lib/api/remote-error";
  import { GlycemicVariabilityStatus } from "$lib/api";
  import { glucoseUnits } from "$lib/stores/appearance-store.svelte";
  import { formatGlucoseValue, getUnitLabel } from "$lib/utils/formatting";
  import TrendingUp from "@lucide/svelte/icons/trending-up";
  import TrendingDown from "@lucide/svelte/icons/trending-down";
  import Minus from "@lucide/svelte/icons/minus";

  const units = $derived(glucoseUnits.current);
  const unitLabel = $derived(getUnitLabel(units));

  const variability = {
    [GlycemicVariabilityStatus.Stable]: { icon: TrendingDown, color: "text-success", label: "Stable" },
    [GlycemicVariabilityStatus.Moderate]: { icon: Minus, color: "text-muted-foreground", label: "Moderate" },
    [GlycemicVariabilityStatus.Variable]: { icon: TrendingUp, color: "text-warning", label: "Variable" },
  };

  // Fetched after hydration, as the other statistics widgets do, so a slow query cannot block SSR.
  let statsPromise = $state<ReturnType<typeof getMultiPeriodStatistics> | null>(null);
  onMount(() => {
    statsPromise = getMultiPeriodStatistics();
  });
</script>

<WidgetCard title="Last 24h summary">
  {#if !statsPromise}
    <WidgetStatSkeleton shape="summary" />
  {:else}
    {#await statsPromise}
      <WidgetStatSkeleton shape="summary" />
    {:then stats}
      {@const day = stats?.lastDay}
      {@const basic = day?.analytics?.basicStats}
      {#if basic && (basic.count ?? 0) > 0}
        {@const gv = day?.analytics?.glycemicVariability}
        {@const status = gv?.coefficientOfVariationStatus ? variability[gv.coefficientOfVariationStatus] : undefined}
        <div class="space-y-2">
          <div class="flex items-baseline justify-between">
            <div>
              <span class="text-2xl font-bold">
                {formatGlucoseValue(basic.mean ?? 0, units)}
              </span>
              <span class="text-xs text-muted-foreground ml-1">{unitLabel} avg</span>
            </div>
            <div class="text-right">
              <span class="text-lg font-semibold text-glucose-in-range">
                {(day?.analytics?.timeInRange?.percentages?.target ?? 0).toFixed(0)}%
              </span>
              <span class="text-xs text-muted-foreground ml-1">TIR</span>
            </div>
          </div>

          <div class="flex justify-between text-xs text-muted-foreground">
            <span>
              ↓ {formatGlucoseValue(basic.min ?? 0, units)}
            </span>
            <span>
              ↑ {formatGlucoseValue(basic.max ?? 0, units)}
            </span>
          </div>

          {#if status}
            <div class="flex items-center justify-between text-xs">
              <span class="text-muted-foreground">Variability:</span>
              <span class="flex items-center gap-1 {status.color}">
                <status.icon class="h-3 w-3" />
                {status.label} ({(gv?.coefficientOfVariation ?? 0).toFixed(0)}% CV)
              </span>
            </div>
          {/if}

          <div class="text-xs text-muted-foreground text-right">
            {basic.count} readings
          </div>
        </div>
      {:else}
        <div class="flex flex-col items-center justify-center text-muted-foreground py-4">
          <p class="text-xs">No data in the last 24h</p>
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
