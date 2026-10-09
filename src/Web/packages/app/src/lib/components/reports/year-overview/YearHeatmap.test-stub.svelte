<script lang="ts">
  import type { DailySummaryDay } from "$api/generated/nocturne-api-client";
  let {
    year,
    yearData,
    sentinelElement = $bindable(),
  }: {
    year: number;
    yearData: Map<number, DailySummaryDay[]>;
    sentinelElement?: HTMLDivElement;
  } = $props();
</script>

<div>
  <div data-year={year} bind:this={sentinelElement}></div>
  <output data-testid={`year-${year}`}>
    {JSON.stringify(yearData.get(year) ?? [])}
  </output>
</div>
