<script lang="ts">
  import WidgetCard from "./WidgetCard.svelte";
  import { getRealtimeStore } from "$lib/stores/realtime-store.svelte";
  import { glucoseUnits } from "$lib/stores/appearance-store.svelte";
  import { formatGlucoseValue, getUnitLabel } from "$lib/utils/formatting";
  import { TrendingUp, TrendingDown, Minus } from "lucide-svelte";

  const realtimeStore = getRealtimeStore();

  // Get units
  const units = $derived(glucoseUnits.current);
  const unitLabel = $derived(getUnitLabel(units));

  // Get glucose target ranges from settings (using safety limits or defaults)
  const targetLow = $derived(70);
  const targetHigh = $derived(180);

  // Calculate daily stats from today's entries
  const dailyStats = $derived.by(() => {
    const now = realtimeStore.now;
    const startOfDay = new Date(now);
    startOfDay.setHours(0, 0, 0, 0);
    const startOfDayMs = startOfDay.getTime();

    const todayEntries = realtimeStore.entries.filter(
      (e) => (e.mills || 0) >= startOfDayMs
    );

    if (todayEntries.length === 0) {
      return null;
    }

    const values = todayEntries
      .map((e) => e.sgv ?? e.mgdl ?? 0)
      .filter((v) => v > 0);

    if (values.length === 0) return null;

    const sum = values.reduce((a, b) => a + b, 0);
    const avg = sum / values.length;
    const min = Math.min(...values);
    const max = Math.max(...values);

    // Calculate TIR
    const inRange = values.filter((v) => v >= targetLow && v <= targetHigh).length;
    const tirPercent = (inRange / values.length) * 100;

    // Standard deviation
    const squareDiffs = values.map((v) => Math.pow(v - avg, 2));
    const avgSquareDiff = squareDiffs.reduce((a, b) => a + b, 0) / values.length;
    const stdDev = Math.sqrt(avgSquareDiff);

    // Coefficient of variation
    const cv = (stdDev / avg) * 100;

    return {
      average: avg,
      min,
      max,
      tir: tirPercent,
      cv,
      readings: values.length,
    };
  });

  // Determine trend icon based on CV
  const TrendIcon = $derived.by(() => {
    if (!dailyStats) return Minus;
    if (dailyStats.cv < 33) return TrendingDown; // Stable (good)
    if (dailyStats.cv > 50) return TrendingUp; // Variable
    return Minus; // Moderate
  });

  const cvStatus = $derived.by(() => {
    if (!dailyStats) return { text: "—", color: "text-muted-foreground" };
    if (dailyStats.cv < 33) return { text: "Stable", color: "text-green-400" };
    if (dailyStats.cv > 50) return { text: "Variable", color: "text-yellow-400" };
    return { text: "Moderate", color: "text-muted-foreground" };
  });
</script>

<WidgetCard title="Today's Summary">
  {#if dailyStats}
    <div class="space-y-2">
      <!-- Average with TIR -->
      <div class="flex items-baseline justify-between">
        <div>
          <span class="text-2xl font-bold">
            {formatGlucoseValue(dailyStats.average, units)}
          </span>
          <span class="text-xs text-muted-foreground ml-1">{unitLabel} avg</span>
        </div>
        <div class="text-right">
          <span class="text-lg font-semibold" style="color: var(--glucose-in-range);">
            {dailyStats.tir.toFixed(0)}%
          </span>
          <span class="text-xs text-muted-foreground ml-1">TIR</span>
        </div>
      </div>

      <!-- Min/Max range -->
      <div class="flex justify-between text-xs text-muted-foreground">
        <span>
          ↓ {formatGlucoseValue(dailyStats.min, units)}
        </span>
        <span>
          ↑ {formatGlucoseValue(dailyStats.max, units)}
        </span>
      </div>

      <!-- CV indicator -->
      <div class="flex items-center justify-between text-xs">
        <span class="text-muted-foreground">Variability:</span>
        <span class="flex items-center gap-1 {cvStatus.color}">
          <TrendIcon class="h-3 w-3" />
          {cvStatus.text} ({dailyStats.cv.toFixed(0)}% CV)
        </span>
      </div>

      <!-- Readings count -->
      <div class="text-xs text-muted-foreground text-right">
        {dailyStats.readings} readings
      </div>
    </div>
  {:else}
    <div class="flex flex-col items-center justify-center text-muted-foreground py-4">
      <p class="text-xs">No data today</p>
    </div>
  {/if}
</WidgetCard>
