<script lang="ts">
  import { GlucoseChartCard } from "$lib/components/dashboard/glucose-chart";
  import { getReportsData } from "$lib/data/reports.remote";
  import { requireDateParamsContext } from "$lib/hooks/date-params.svelte";
  import { contextResource } from "$lib/hooks/resource-context.svelte";

  // Get shared date params from context (set by reports layout)
  // Default: 7 days for day-by-day readings view
  const reportsParams = requireDateParamsContext(7);

  // Create resource with automatic layout registration
  const reportsResource = contextResource(
    () => getReportsData(reportsParams.dateRangeInput),
    { errorTitle: "Error Loading Readings" }
  );
</script>

{#if reportsResource.current}
  <GlucoseChartCard
    dateRange={reportsResource.current.dateRange}
  />
{/if}
