<script lang="ts">
  import BasalAnalysisContent from "./BasalAnalysisContent.svelte";
  import { requireDateParamsContext } from "#lib/hooks/date-params.svelte.js";

  // Get shared date params from context (set by reports layout)
  // Default: 14 days for basal pattern analysis
  const reportsParams = requireDateParamsContext(14);

  // Date info derived from the URL params (same shape contextResource.date exposes).
  const dateInfo = $derived({
    from: reportsParams.startDate,
    to: reportsParams.endDate,
    dayCount: reportsParams.dayCount,
  });

  const range = $derived({ from: reportsParams.fromDay, to: reportsParams.toDay });

  const rangeKey = $derived(JSON.stringify(range));
</script>

{#key rangeKey}
  <BasalAnalysisContent {range} {dateInfo} />
{/key}
