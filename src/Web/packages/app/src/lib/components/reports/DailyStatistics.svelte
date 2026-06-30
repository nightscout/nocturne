<script lang="ts">
  import type { Thresholds, DayToDayDailyData } from "./types";
  import { getGlucoseColor } from "$lib/utils/glucose-analytics.ts";
  import { bg, bgLabel, bgRange } from "$lib/utils/formatting";

  interface Props {
    dayData: DayToDayDailyData;
    thresholds: Thresholds;
  }

  let { dayData, thresholds }: Props = $props();
</script>

<div class="@container mt-4">
<div class="grid grid-cols-2 @lg:grid-cols-3 gap-4 text-sm">
  <div class="bg-gray-50 p-3 rounded">
    <div class="text-gray-600 text-xs">Average</div>
    <div
      class="font-semibold {getGlucoseColor(
        dayData.analytics?.basicStats?.mean ?? 0,
        thresholds
      )}"
    >
      {bg(dayData.analytics?.basicStats?.mean ?? 0)} {bgLabel()}
    </div>
  </div>
  <div class="bg-gray-50 p-3 rounded">
    <div class="text-gray-600 text-xs">Range</div>
    <div class="font-semibold">
      {bg(dayData.analytics?.basicStats?.min ?? 0)} - {bg(
        dayData.analytics?.basicStats?.max ?? 0
      )}
    </div>
  </div>
  <div class="bg-gray-50 p-3 rounded">
    <div class="text-gray-600 text-xs">Time in Range</div>
    <div class="font-semibold text-green-600">
      {dayData.analytics?.timeInRange?.percentages?.target ?? 0}%
    </div>
    <div class="text-xs text-gray-500">
      ({bgRange(thresholds.targetBottom, thresholds.targetTop)})
    </div>
  </div>
  <div class="bg-gray-50 p-3 rounded">
    <div class="text-gray-600 text-xs">Tight Time in Range</div>
    <div class="font-semibold text-blue-600">
      <!-- Use tight target percentage if available, otherwise fall back to regular target -->
      {dayData.analytics?.timeInRange?.percentages?.tightTarget ??
        dayData.analytics?.timeInRange?.percentages?.target ??
        0}%
    </div>
    <div class="text-xs text-gray-500">
      ({bgRange(thresholds.targetBottom, thresholds.tightTargetTop)})
    </div>
  </div>
  <div class="bg-gray-50 p-3 rounded">
    <div class="text-gray-600 text-xs">Low Events</div>
    <div class="font-semibold text-red-600">
      {(dayData.analytics?.timeInRange?.percentages?.low ?? 0) +
        (dayData.analytics?.timeInRange?.percentages?.veryLow ?? 0)}%
    </div>
  </div>
  <div class="bg-gray-50 p-3 rounded">
    <div class="text-gray-600 text-xs">High Events</div>
    <div class="font-semibold text-orange-600">
      {(dayData.analytics?.timeInRange?.percentages?.high ?? 0) +
        (dayData.analytics?.timeInRange?.percentages?.veryHigh ?? 0)}%
    </div>
  </div>
</div>
</div>
