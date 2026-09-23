<script lang="ts">
  import {
    formatPercentageDisplay,
    formatInsulinDisplay,
    formatCarbDisplay,
    bgRange,
    type OverallAverages,
  } from "$lib/utils/formatting";
  import type { Thresholds } from "./types";

  interface Props {
    averages: OverallAverages;
    thresholds: Thresholds;
  }

  let { averages, thresholds }: Props = $props();
</script>

<div class="@container mb-6">
<div class="bg-white shadow-lg rounded-lg p-4 @lg:p-6">
  <h2 class="text-xl font-semibold text-foreground mb-4">Period Averages</h2>
  <div class="bg-green-50 border border-green-200 rounded-lg p-4 mb-4">
    <h3 class="text-sm font-semibold text-foreground mb-3">Glucose Management</h3>
    <div class="grid grid-cols-1 @lg:grid-cols-2 gap-4 text-sm">
      <div class="space-y-2">
        <div class="flex justify-between">
          <span class="text-muted-foreground">Time in Range average:</span>
          <span class="font-semibold text-glucose-in-range">
            {formatPercentageDisplay(averages.avgTimeInRange)}%
          </span>
        </div>
        <div class="flex justify-between">
          <span class="text-muted-foreground">Tight Time in Range average:</span>
          <span class="font-semibold text-glucose-tight-range">
            {formatPercentageDisplay(averages.avgTightTimeInRange)}%
          </span>
        </div>
      </div>
      <div class="text-xs text-muted-foreground space-y-1">
        <div>
          TIR: {bgRange(thresholds.targetBottom, thresholds.targetTop)}
        </div>
        <div>
          TTIR: {bgRange(thresholds.targetBottom, thresholds.tightTargetTop)}
        </div>
      </div>
    </div>
  </div>
  <div class="bg-blue-50 border border-blue-200 rounded-lg p-4">
    <h3 class="text-sm font-semibold text-foreground mb-3">
      Insulin & Nutrition
    </h3>
    <div class="grid grid-cols-1 @lg:grid-cols-2 gap-4 text-sm">
      <div class="space-y-2">
        <div class="flex justify-between">
          <span class="text-muted-foreground">TDD average:</span>
          <span class="font-semibold">
            {formatInsulinDisplay(averages.avgTotalDaily)}U
          </span>
        </div>
        <div class="flex justify-between">
          <span class="text-muted-foreground">Bolus average:</span>
          <span class="font-medium">
            {formatPercentageDisplay(averages.bolusPercentage)}%
          </span>
        </div>
        <div class="flex justify-between">
          <span class="text-muted-foreground">Basal average:</span>
          <span class="font-medium">
            {formatPercentageDisplay(averages.basalPercentage)}%
          </span>
        </div>
        <div class="flex justify-between">
          <span class="text-muted-foreground">(Base basal average:</span>
          <span class="font-medium">
            {formatPercentageDisplay(averages.basalPercentage)}%)
          </span>
        </div>
      </div>
      <div class="space-y-2">
        <div class="flex justify-between">
          <span class="text-muted-foreground">Carbs average:</span>
          <span class="font-medium">
            {formatCarbDisplay(averages.avgCarbs)}g
          </span>
        </div>
        <div class="flex justify-between">
          <span class="text-muted-foreground">Protein average:</span>
          <span class="font-medium">
            {formatCarbDisplay(averages.avgProtein)}g
          </span>
        </div>
        <div class="flex justify-between">
          <span class="text-muted-foreground">Fat average:</span>
          <span class="font-medium">
            {formatCarbDisplay(averages.avgFat)}g
          </span>
        </div>
      </div>
    </div>
  </div>
</div>
</div>
