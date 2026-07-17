<script lang="ts">
  import BasalAnalysisContent from "./BasalAnalysisContent.svelte";
  import { requireDateParamsContext } from "$lib/hooks/date-params.svelte";

  // Get shared date params from context (set by reports layout)
  // Default: 14 days for basal pattern analysis
  const reportsParams = requireDateParamsContext(14);

  // Date info derived from the URL params (same shape contextResource.date exposed).
  const dateInfo = $derived.by(() => {
    const range = reportsParams.getDateRange();
    const ms = range.end.getTime() - range.start.getTime();
    return {
      from: range.start,
      to: range.end,
      dayCount: Math.max(1, Math.round(ms / (1000 * 60 * 60 * 24))),
    };
  });

  // ISO strings rather than Date objects: remote-query arguments are devalue
  // serialised and the server schema is z.coerce.date(), which parses them back.
  const analysisDates = $derived.by(() => {
    const range = reportsParams.getDateRange();
    return {
      startDate: range.start.toISOString() as unknown as Date,
      endDate: range.end.toISOString() as unknown as Date,
    };
  });

  const rangeKey = $derived(JSON.stringify(reportsParams.dateRangeInput ?? {}));
</script>

<svelte:head>
  <title>Basal Rate Analysis - Nocturne Reports</title>
  <meta
    name="description"
    content="Analyze your basal insulin delivery patterns with percentile visualization"
  />
</svelte:head>

{#key rangeKey}
  <BasalAnalysisContent {analysisDates} {dateInfo} />
{/key}
