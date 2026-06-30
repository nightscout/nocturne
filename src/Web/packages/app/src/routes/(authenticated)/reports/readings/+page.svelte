<script lang="ts">
  import { GlucoseChartCard } from "$lib/components/dashboard/glucose-chart";
  import { requireDateParamsContext } from "$lib/hooks/date-params.svelte";
  import { useResourceContext } from "$lib/hooks/resource-context.svelte";

  // Get shared date params from context (set by reports layout)
  // Default: 7 days for day-by-day readings view
  const reportsParams = requireDateParamsContext(7);

  // GlucoseChartCard self-fetches its own data; this page only supplies the range.
  const dateRange = $derived.by(() => {
    const { start, end } = reportsParams.getDateRange();
    return { from: start, to: end };
  });

  // No page-level fetch, so report ready state to the layout ResourceGuard directly.
  useResourceContext({
    loading: () => false,
    error: () => null,
    hasData: () => true,
    errorTitle: "Error Loading Readings",
    refetch: () => {},
  });
</script>

<div class="@container p-3 @md:p-6">
  <GlucoseChartCard {dateRange} />
</div>
