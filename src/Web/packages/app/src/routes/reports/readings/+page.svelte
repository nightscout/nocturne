<script lang="ts">
  import GlucoseChartCard from "$lib/components/dashboard/GlucoseChartCard.svelte";
  import ReportsSkeleton from "$lib/components/reports/ReportsSkeleton.svelte";
  import { getReportsData } from "$lib/data/reports.remote";
  import { requireDateParamsContext } from "$lib/hooks/date-params.svelte";
  import { resource } from "runed";

  // Get shared date params from context (set by reports layout)
  // Default: 7 days for day-by-day readings view
  const reportsParams = requireDateParamsContext(7);

  // Use resource for controlled reactivity - prevents excessive re-fetches
  const reportsResource = resource(
    () => reportsParams.dateRangeInput,
    async (dateRangeInput) => {
      return await getReportsData(dateRangeInput);
    },
    { debounce: 100 }
  );

  // Loading state
  const isLoading = $derived(reportsResource.loading);
</script>

{#if isLoading && !reportsResource.current}
  <ReportsSkeleton />
{:else if reportsResource.error}
  <div class="p-4 text-center">
    <p class="text-destructive">
      {reportsResource.error instanceof Error ? reportsResource.error.message : String(reportsResource.error)}
    </p>
  </div>
{:else if reportsResource.current}
  <GlucoseChartCard
    entries={reportsResource.current.entries}
    treatments={reportsResource.current.treatments}
    dateRange={reportsResource.current.dateRange}
  />
{/if}
