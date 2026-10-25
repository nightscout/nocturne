<script lang="ts">
  import { getReportsToday } from "$api/reports.remote";
  import { contextResource } from "$lib/hooks/resource-context.svelte";
  import ComparisonReport from "./ComparisonReport.svelte";

  const today = contextResource(() => getReportsToday(), {
    errorTitle: "Error loading comparison",
  });
</script>

{#if today.current}
  <ComparisonReport today={today.current} />
{/if}
