<script lang="ts">
  import { GlucoseChartCard } from "#lib/components/dashboard/glucose-chart/index.js";
  import { getReportRange } from "#lib/api/reports.remote.js";
  import { requireDateParamsContext } from "#lib/hooks/date-params.svelte.js";
  import { contextResource } from "#lib/hooks/resource-context.svelte.js";

  // Get shared date params from context (set by reports layout)
  // Default: 7 days for day-by-day readings view
  const reportsParams = requireDateParamsContext(7);

  // GlucoseChartCard self-fetches its own data; this page only supplies the range,
  // read in the patient's zone by the server.
  const rangeResource = contextResource(
    () => getReportRange({ from: reportsParams.fromDay, to: reportsParams.toDay }),
    { errorTitle: "Error Loading Readings" }
  );
  const dateRange = $derived(
    rangeResource.current
      ? { from: rangeResource.current.startDate, to: rangeResource.current.endDate }
      : undefined
  );
</script>

<div class="@container">
  {#if dateRange}
    <GlucoseChartCard {dateRange} heightClass="h-[400px] @md:h-[450px] print:h-[620px]" />
  {/if}
</div>
