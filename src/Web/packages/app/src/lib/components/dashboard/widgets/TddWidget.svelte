<script lang="ts">
  import { onMount } from "svelte";
  import WidgetCard from "./WidgetCard.svelte";
  import { PieChart } from "layerchart";
  import { getMultiPeriodStatistics } from "$api/generated/statistics.generated.remote";
  import { Button } from "$lib/components/ui/button";
  import ReliabilityBadge from "$lib/components/reports/ReliabilityBadge.svelte";
  import { remoteErrorMessage } from "$lib/api/remote-error";

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

<WidgetCard title="Total Daily Dose">
  {#snippet subtitleSnippet()}
    <Button
      variant="ghost-muted"
      size="xs"
      class="-mr-2"
      onclick={toggleView}
    >
      {showAverage ? "90-Day Avg" : "Today"}
    </Button>
  {/snippet}

  {#if !statsPromise}
    <div class="flex items-center justify-center py-4">
      <div class="h-4 w-4 animate-spin rounded-full border-2 border-primary border-t-transparent"></div>
    </div>
  {:else}
    {#await statsPromise}
      <div class="flex items-center justify-center py-4">
        <div class="h-4 w-4 animate-spin rounded-full border-2 border-primary border-t-transparent"></div>
      </div>
    {:then stats}
    {@const todayDelivery = stats?.lastDay?.insulinDelivery}
    {@const avgDelivery = stats?.last90Days?.insulinDelivery}
    {@const todayCarbs = stats?.lastDay?.treatmentSummary?.totals?.food?.carbs ?? 0}
    {@const avgCarbs = (stats?.last90Days?.treatmentSummary?.totals?.food?.carbs ?? 0) / (stats?.last90Days?.periodDays ?? 90)}

    <!-- Use insulinDelivery for insulin values (single source of truth) -->
    {@const todayBolus = todayDelivery?.totalBolus ?? 0}
    {@const todayAuto = todayDelivery?.microBolusInsulin ?? 0}
    {@const todayBasal = Math.max(0, (todayDelivery?.totalBasal ?? 0) - todayAuto)}
    {@const todayTotal = todayDelivery?.tdd ?? (todayBolus + todayAuto + todayBasal)}

    {@const periodDays = stats?.last90Days?.periodDays ?? 90}
    {@const avgBolus = (avgDelivery?.totalBolus ?? 0) / periodDays}
    {@const avgAuto = (avgDelivery?.microBolusInsulin ?? 0) / periodDays}
    {@const avgBasal = Math.max(0, ((avgDelivery?.totalBasal ?? 0) - (avgDelivery?.microBolusInsulin ?? 0)) / periodDays)}
    {@const avgTotal = avgDelivery?.tdd ?? 0}

    <!-- Select which values to display based on toggle -->
    {@const bolus = showAverage ? avgBolus : todayBolus}
    {@const auto = showAverage ? avgAuto : todayAuto}
    {@const basal = showAverage ? avgBasal : todayBasal}
    {@const total = showAverage ? avgTotal : todayTotal}
    {@const carbs = showAverage ? avgCarbs : todayCarbs}

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
      <ReliabilityBadge reliability={showAverage ? stats?.last90Days?.reliability : stats?.lastDay?.reliability} />
    {:else}
      <div class="flex flex-col items-center justify-center text-muted-foreground py-4">
        <p class="text-xs">{showAverage ? "No data for 90-day average" : "No data today"}</p>
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
