<script lang="ts">
  import { GlucoseChart } from "$lib/components/glucose-chart";
  import { getReportsData } from "$lib/data/reports.remote";
  import { useDateRange } from "$lib/hooks/use-date-range.svelte.js";

  // Build date range input from URL parameters
  const dateRangeInput = $derived(useDateRange());

  // Query for reports data
  const reportsQuery = $derived(getReportsData(dateRangeInput));
  const data = $derived(await reportsQuery);
</script>

<svelte:boundary>
  {#snippet pending()}
    <div class="flex items-center justify-center h-64">
      <div class="text-center space-y-4">
        <div
          class="animate-spin rounded-full h-8 w-8 border-b-2 border-primary mx-auto"
        ></div>
        <p class="text-muted-foreground">Loading readings...</p>
      </div>
    </div>
  {/snippet}

  {#snippet failed(error)}
    <div class="p-4 text-center">
      <p class="text-destructive">
        {error instanceof Error ? error.message : String(error)}
      </p>
    </div>
  {/snippet}

  <GlucoseChart
    entries={data.entries}
    treatments={data.treatments}
    dateRange={data.dateRange}
  />
</svelte:boundary>
