<script lang="ts">
  import { getRealtimeStore } from "$lib/stores/realtime-store.svelte";
  import {
    glucoseUnits,
    predictionMinutes,
    predictionEnabled,
  } from "$lib/stores/appearance-store.svelte";
  import {
    formatGlucoseValue,
    formatGlucoseDelta,
    convertToDisplayUnits,
  } from "$lib/utils/formatting";
  import { GlucoseValueIndicator } from "$lib/components/shared";
  import { Badge } from "$lib/components/ui/badge";
  import { Chart, Svg, Spline, Rule } from "layerchart";

  import {
    getPredictions,
    type PredictionData,
  } from "$api/predictions.remote";
  import { getDirectionInfo } from "$lib/utils";

  const realtimeStore = getRealtimeStore();

  // Get current glucose values (raw mg/dL)
  const rawCurrentBG = $derived(realtimeStore.currentBG);
  const rawBgDelta = $derived(realtimeStore.bgDelta);
  const direction = $derived(realtimeStore.direction);
  const demoMode = $derived(realtimeStore.demoMode);
  const lastUpdated = $derived(realtimeStore.lastUpdated);

  // Connection status
  const isConnected = $derived(realtimeStore.isConnected);

  // Stale threshold in milliseconds (10 minutes)
  const STALE_THRESHOLD_MS = 10 * 60 * 1000;

  // Prediction horizon in milliseconds
  const predictionHorizonMs = $derived(predictionMinutes.current * 60 * 1000);

  // Stale detection - data older than threshold (recalculates every second)
  const now = $derived(realtimeStore.now);
  const isStale = $derived(now - lastUpdated > STALE_THRESHOLD_MS);
  const isDisconnected = $derived(!isConnected);

  // Loading state - no data received yet
  const isLoading = $derived(
    rawCurrentBG === 0 && realtimeStore.entries.length === 0
  );

  // Format for display based on user's unit preference
  const units = $derived(glucoseUnits.current);
  const displayBG = $derived(formatGlucoseValue(rawCurrentBG, units));
  const displayDelta = $derived(formatGlucoseDelta(rawBgDelta, units));

  // Oref prediction data from backend
  let orefPredictions = $state<PredictionData | null>(null);

  // Fetch oref predictions (re-fetch when glucose data meaningfully changes)
  $effect(() => {
    // Depend on current BG - this changes when new data arrives
    const currentBG = rawCurrentBG;
    const stale = isStale;
    const disconnected = isDisconnected;

    // Only fetch if we have valid glucose data
    if (currentBG > 0 && !stale && !disconnected) {
      let active = true;

      getPredictions({})
        .then((data) => {
          if (active) {
            orefPredictions = data;
          }
        })
        .catch((err) => {
          if (active) {
            console.error("Failed to fetch predictions for sidebar:", err);
            orefPredictions = null;
          }
        });

      return () => {
        active = false;
      };
    }
  });

  // Get last 3 hours of entries for mini chart (convert to display units)
  const chartEntries = $derived.by(() => {
    const threeHoursAgo = Date.now() - 3 * 60 * 60 * 1000;
    return realtimeStore.entries
      .filter((e) => (e.mills ?? 0) > threeHoursAgo)
      .map((e) => ({
        date: new Date(e.mills ?? 0),
        value: convertToDisplayUnits(e.sgv ?? e.mgdl ?? 0, units),
      }))
      .sort((a, b) => a.date.getTime() - b.date.getTime());
  });

  // Extract 30-minute prediction from oref data (using main curve)
  const predictionData = $derived.by(() => {
    if (
      !orefPredictions?.curves?.main ||
      orefPredictions.curves.main.length === 0
    ) {
      return [];
    }

    const baseTime = orefPredictions.timestamp.getTime();
    const cutoffTime = baseTime + predictionHorizonMs;

    // Filter to only include points within 30-minute horizon
    return orefPredictions.curves.main
      .filter((p) => p.timestamp <= cutoffTime)
      .map((p) => ({
        date: new Date(p.timestamp),
        value: convertToDisplayUnits(p.value, units),
      }));
  });

  // Y domain for chart (unit-aware, include prediction values)
  const isMMOL = $derived(units === "mmol");
  const yMax = $derived.by(() => {
    const allValues = [
      ...chartEntries.map((e) => e.value),
      ...predictionData.map((p) => p.value),
    ];
    if (allValues.length === 0) return isMMOL ? 16.7 : 300;
    const maxPadding = isMMOL ? 1.5 : 30;
    return isMMOL
      ? Math.min(22.2, Math.max(...allValues) + maxPadding)
      : Math.min(400, Math.max(...allValues) + maxPadding);
  });
  const yMin = $derived(isMMOL ? 2.2 : 40);

  // X domain for chart (extend 30 minutes into future when prediction is available)
  const xDomain = $derived.by(() => {
    if (chartEntries.length === 0) return undefined;
    const minTime = chartEntries[0].date;
    const maxTime =
      predictionData.length > 0
        ? predictionData[predictionData.length - 1].date
        : chartEntries[chartEntries.length - 1].date;
    return [minTime, maxTime] as [Date, Date];
  });

  const DirectionIcon = $derived(getDirectionInfo(direction).icon);

  // Calculate time since last reading for display
  const timeSince = $derived(realtimeStore.timeSinceReading);

  // Syncing state
  const isSyncing = $derived(realtimeStore.isSyncing);

  // Status text - show connection error or time since reading
  // Note: "Syncing..." state is handled in the GlucoseValueIndicator component
  const statusText = $derived(isDisconnected ? "Connection Error" : timeSince);
</script>

<div class="space-y-3 group-data-[collapsible=icon]:hidden">
  <!-- Current BG Display -->
  <div class="flex items-center justify-between">
    <div class="flex items-center gap-2">
      <GlucoseValueIndicator
        displayValue={displayBG}
        rawBgMgdl={rawCurrentBG}
        {isLoading}
        {isStale}
        {isDisconnected}
        {isSyncing}
        {statusText}
        statusTooltip="Click to sync data"
        onSyncClick={() => realtimeStore.syncData()}
        size="sm"
      />
      <div class="flex flex-col items-start">
        <div class="flex items-center gap-1">
          <DirectionIcon class="h-5 w-5" />
          <span class="text-sm font-medium">
            {displayDelta}
          </span>
        </div>
      </div>
    </div>
    {#if demoMode}
      <Badge variant="secondary" class="text-xs">Demo</Badge>
    {/if}
  </div>

  <!-- Mini Chart (clickable link to dashboard) -->
  <a
    href="/"
    class="block h-16 w-full rounded-md bg-card border border-border overflow-hidden hover:border-primary/50 transition-colors"
  >
    {#if chartEntries.length > 1}
      <Chart
        data={chartEntries}
        x="date"
        y="value"
        {xDomain}
        yDomain={[yMin, yMax]}
        padding={{ top: 2, bottom: 2, left: 2, right: 2 }}
      >
        <Svg>
          <!-- Target range lines (75 mg/dl = 4.2 mmol, 180 mg/dl = 10 mmol) -->
          <Rule
            y={convertToDisplayUnits(70, units)}
            class="stroke-yellow-500/40"
          />
          <Rule
            y={convertToDisplayUnits(180, units)}
            class="stroke-orange-500/40"
          />

          <!-- Glucose line -->
          <Spline class="stroke-primary stroke-2 fill-none" />

          <!-- Prediction line (dashed) -->
          {#if predictionData.length > 1 && predictionEnabled.current}
            <Spline
              data={predictionData}
              class="stroke-primary/50 stroke-2 fill-none [stroke-dasharray:4]"
            />
          {/if}
        </Svg>
      </Chart>
    {:else}
      <div
        class="h-full flex items-center justify-center text-xs text-muted-foreground"
      >
        Waiting for data...
      </div>
    {/if}
  </a>
</div>

<!-- Collapsed state: just show current BG with shared component styling -->
<div class="hidden group-data-[collapsible=icon]:flex justify-center">
  <GlucoseValueIndicator
    displayValue={displayBG}
    rawBgMgdl={rawCurrentBG}
    {isLoading}
    {isStale}
    {isDisconnected}
    size="xs"
    class="text-lg"
  />
</div>
