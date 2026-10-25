<script lang="ts">
  import type { DailySummaryDay } from "$api/generated/nocturne-api-client";
  let {
    year,
    yearData,
    glucoseBands,
    sentinelElement = $bindable(),
  }: {
    year: number;
    yearData: Map<number, DailySummaryDay[]>;
    glucoseBands: readonly number[];
    sentinelElement?: HTMLDivElement;
  } = $props();
</script>

<div>
  <div data-year={year} bind:this={sentinelElement}></div>
  <output data-testid={`bands-${year}`}>{glucoseBands.join(",")}</output>
  <output data-testid={`year-${year}`}>
    {JSON.stringify(yearData.get(year) ?? [])}
  </output>
</div>
