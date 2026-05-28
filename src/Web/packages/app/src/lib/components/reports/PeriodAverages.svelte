<script lang="ts">
  import {
    formatPercentageDisplay,
    formatInsulinDisplay,
    formatCarbDisplay,
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
  <h2 class="text-xl font-semibold text-gray-700 mb-4">Period Averages</h2>
  <div class="bg-green-50 border border-green-200 rounded-lg p-4 mb-4">
    <h3 class="text-sm font-semibold text-gray-700 mb-3">Glucose Management</h3>
    <div class="grid grid-cols-1 @lg:grid-cols-2 gap-4 text-sm">
      <div class="space-y-2">
        <div class="flex justify-between">
          <span class="text-gray-600">Time in Range average:</span>
          <span class="font-semibold text-green-600">
            {formatPercentageDisplay(averages.avgTimeInRange)}%
          </span>
        </div>
        <div class="flex justify-between">
          <span class="text-gray-600">Tight Time in Range average:</span>
          <span class="font-semibold text-blue-600">
            {formatPercentageDisplay(averages.avgTightTimeInRange)}%
          </span>
        </div>
      </div>
      <div class="text-xs text-gray-500 space-y-1">
        <div>
          TIR: {thresholds.targetBottom}-{thresholds.targetTop} mg/dL
        </div>
        <div>
          TTIR: {thresholds.targetBottom}-{thresholds.tightTargetTop} mg/dL
        </div>
      </div>
    </div>
  </div>
  <div class="bg-blue-50 border border-blue-200 rounded-lg p-4">
    <h3 class="text-sm font-semibold text-gray-700 mb-3">
      Insulin & Nutrition
    </h3>
    <div class="grid grid-cols-1 @lg:grid-cols-2 gap-4 text-sm">
      <div class="space-y-2">
        <div class="flex justify-between">
          <span class="text-gray-600">TDD average:</span>
          <span class="font-semibold">
            {formatInsulinDisplay(averages.avgTotalDaily)}U
          </span>
        </div>
        <div class="flex justify-between">
          <span class="text-gray-600">Bolus average:</span>
          <span class="font-medium">
            {formatPercentageDisplay(averages.bolusPercentage)}%
          </span>
        </div>
        <div class="flex justify-between">
          <span class="text-gray-600">Basal average:</span>
          <span class="font-medium">
            {formatPercentageDisplay(averages.basalPercentage)}%
          </span>
        </div>
        <div class="flex justify-between">
          <span class="text-gray-600">(Base basal average:</span>
          <span class="font-medium">
            {formatPercentageDisplay(averages.basalPercentage)}%)
          </span>
        </div>
      </div>
      <div class="space-y-2">
        <div class="flex justify-between">
          <span class="text-gray-600">Carbs average:</span>
          <span class="font-medium">
            {formatCarbDisplay(averages.avgCarbs)}g
          </span>
        </div>
        <div class="flex justify-between">
          <span class="text-gray-600">Protein average:</span>
          <span class="font-medium">
            {formatCarbDisplay(averages.avgProtein)}g
          </span>
        </div>
        <div class="flex justify-between">
          <span class="text-gray-600">Fat average:</span>
          <span class="font-medium">
            {formatCarbDisplay(averages.avgFat)}g
          </span>
        </div>
      </div>
    </div>
  </div>
</div>
</div>
