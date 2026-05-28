<script lang="ts">
  import { formatGlucoseValue } from "$lib/utils/formatting";

  let {
    monthSummary,
    units,
    unitLabel,
  } = $props<{
    monthSummary: {
      totalReadings: number;
      inRangePercent: number;
      avgGlucose: number;
      avgDailyCarbs: number;
      tdd: number;
    };
    units: string;
    unitLabel: string;
  }>();
</script>

{#if monthSummary.totalReadings > 0}
  <div class="@container mt-4 pt-4 border-t">
  <div class="grid grid-cols-2 @lg:grid-cols-4 gap-4">
    <div class="text-center">
      <div class="text-2xl font-bold text-glucose-in-range">
        {monthSummary.inRangePercent.toFixed(1)}%
      </div>
      <div class="text-sm text-muted-foreground">Avg TIR</div>
    </div>
    <div class="text-center">
      <div class="text-2xl font-bold">
        {formatGlucoseValue(monthSummary.avgGlucose, units)}
        <span class="text-sm font-normal text-muted-foreground">
          {unitLabel}
        </span>
      </div>
      <div class="text-sm text-muted-foreground">Avg Glucose</div>
    </div>
    <div class="text-center">
      <div class="text-2xl font-bold">
        {monthSummary.avgDailyCarbs.toFixed(0)}g
      </div>
      <div class="text-sm text-muted-foreground">
        Avg Daily Carbs
      </div>
    </div>
    <div class="text-center">
      <div class="text-2xl font-bold">
        {monthSummary.tdd.toFixed(1)}U
      </div>
      <div class="text-sm text-muted-foreground">
        Avg TDD
      </div>
    </div>
  </div>
  </div>
{/if}
