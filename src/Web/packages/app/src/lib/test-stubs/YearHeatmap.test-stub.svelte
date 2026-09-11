<script lang="ts">
  import type { DailySummaryDay } from "$api/generated/nocturne-api-client";

  let {
    year,
    yearData,
    transformYearData,
    getCellFill,
    sentinelElement = $bindable(),
  }: {
    year: number;
    yearData: Map<number, DailySummaryDay[]>;
    transformYearData: (days: DailySummaryDay[]) => { dateString: string }[];
    getCellFill: (day: { dateString: string }) => string;
    sentinelElement?: HTMLDivElement;
  } = $props();
</script>

<div bind:this={sentinelElement} data-year={year}>
  {#each transformYearData(yearData.get(year) ?? []) as day}
    <span data-testid="cell-{day.dateString}">{getCellFill(day)}</span>
  {/each}
</div>
