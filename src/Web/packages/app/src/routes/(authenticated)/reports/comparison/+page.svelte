<script lang="ts">
  import { getReportsToday } from "#lib/api/reports.remote.js";
  import { contextResource } from "#lib/hooks/resource-context.svelte.js";
  import ComparisonReport from "./ComparisonReport.svelte";

  const today = contextResource(() => getReportsToday(), {
    errorTitle: "Error loading comparison",
  });
</script>

{#if today.current}
  <ComparisonReport today={today.current} />
{/if}
