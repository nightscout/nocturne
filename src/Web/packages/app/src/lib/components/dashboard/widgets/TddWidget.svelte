<script lang="ts">
  import { onMount } from "svelte";
  import WidgetCard from "./WidgetCard.svelte";
  import WidgetStatSkeleton from "./WidgetStatSkeleton.svelte";
  import { PieChart } from "layerchart";
  import { getMultiPeriodStatistics } from "$api/generated/statistics.generated.remote";
  import { Button } from "$lib/components/ui/button";
  import ReliabilityBadge from "$lib/components/reports/ReliabilityBadge.svelte";
  import { remoteErrorMessage } from "$lib/api/remote-error";

  // Toggle between the last 24 hours and the 90-day average
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

<WidgetCard title="Total Daily Dose">
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
    <WidgetStatSkeleton shape="tdd" />
  {:else}
    {#await statsPromise}
      <WidgetStatSkeleton shape="tdd" />
    {:then stats}
    {@const view = showAverage ? stats?.last90Days : stats?.lastDay}
    {@const delivery = view?.insulinDelivery}
    {@const bolus = delivery?.manualBolusPerDay ?? 0}
    {@const auto = delivery?.automaticBolusPerDay ?? 0}
    {@const basal = delivery?.totalBasalPerDay ?? 0}
    {@const total = delivery?.tdd ?? 0}
    {@const carbs = view?.treatmentSummary?.dailyCarbs ?? 0}

    {#if total > 0 || carbs > 0}
      {@const segmentData = [
        { key: "Bolus", value: bolus, color: "var(--iob-bolus)" },
        { key: "Auto", value: auto, color: "var(--iob-temporary)" },
        { key: "Basal", value: basal, color: "var(--basal)" },
      ].filter(s => s.value > 0)}

      <div class="flex items-center justify-between gap-4">
        <div class="min-w-0">
          {#if total > 0}
            <p class="flex items-baseline gap-1">
              <span class="text-xl font-semibold tabular-nums">{total.toFixed(1)}</span>
              <span class="text-xs text-muted-foreground">U</span>
            </p>
          {:else}
            <p class="text-sm text-muted-foreground">No insulin</p>
          {/if}
          <dl class="mt-1 grid grid-cols-[auto_auto] gap-x-3 gap-y-0.5 text-xs tabular-nums">
            <dt class="flex items-center gap-1.5 text-muted-foreground"><span class="size-2 rounded-full bg-iob-bolus"></span>Bolus</dt>
            <dd class="m-0 text-right">{bolus.toFixed(1)} U</dd>
            {#if auto > 0}
              <dt class="flex items-center gap-1.5 text-muted-foreground"><span class="size-2 rounded-full bg-iob-temporary"></span>Auto</dt>
              <dd class="m-0 text-right">{auto.toFixed(1)} U</dd>
            {/if}
            <dt class="flex items-center gap-1.5 text-muted-foreground"><span class="size-2 rounded-full bg-basal"></span>Basal</dt>
            <dd class="m-0 text-right">{basal.toFixed(1)} U</dd>
            <dt class="flex items-center gap-1.5 text-muted-foreground"><span class="size-2 rounded-full bg-carbs"></span>Carbs</dt>
            <dd class="m-0 text-right">{carbs.toFixed(0)} g</dd>
            <!-- Holds the Auto row's height for accounts without automatic boluses, so the widget is as tall as the skeleton. -->
            {#if auto <= 0}
              <dt class="invisible" aria-hidden="true">&nbsp;</dt>
              <dd class="invisible m-0" aria-hidden="true">&nbsp;</dd>
            {/if}
          </dl>
        </div>
        {#if total > 0}
          <div class="size-16 shrink-0" aria-hidden="true">
            <PieChart
              data={segmentData}
              key="key"
              value="value"
              cRange={segmentData.map(s => s.color)}
              innerRadius={-10}
              cornerRadius={2}
              padAngle={0.02}
            />
          </div>
        {/if}
      </div>
      <ReliabilityBadge reliability={view?.reliability} />
    {:else}
      <div class="flex flex-col items-center justify-center text-muted-foreground py-4">
        <p class="text-xs">{showAverage ? "No data for 90-day average" : "No data in the last 24h"}</p>
      </div>
    {/if}
    {:catch err}
      <div class="flex flex-col items-center justify-center text-muted-foreground py-4">
        <p class="text-xs">Failed to load data</p>
        <p class="text-xs text-destructive">{remoteErrorMessage(err, "Please try again.")}</p>
      </div>
    {/await}
  {/if}
</WidgetCard>
