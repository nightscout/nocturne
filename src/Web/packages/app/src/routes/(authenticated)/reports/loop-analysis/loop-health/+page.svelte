<script lang="ts">
  import { getLoopHealth } from "$api/generated/loopAnalysis.generated.remote";
  import { contextResource } from "$lib/hooks/resource-context.svelte";
  import { getLoopAnalysis } from "$lib/components/reports/loop-analysis/loop-analysis-context.svelte";
  import PageBodyLoading from "$lib/components/reports/loop-analysis/PageBodyLoading.svelte";
  import LoopHealthBody from "$lib/components/reports/loop-analysis/LoopHealthBody.svelte";

  const analysis = getLoopAnalysis();
  const resource = contextResource(() => getLoopHealth(analysis.query), { errorTitle: "Cannot load the loop health" });
</script>

{#if resource.current}
  <LoopHealthBody response={resource.current} />
{:else}
  <PageBodyLoading />
{/if}
