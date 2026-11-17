<script lang="ts">
  import { getLoopHealth } from "#lib/api/generated/loopAnalysis.generated.remote.js";
  import { contextResource } from "#lib/hooks/resource-context.svelte.js";
  import { getLoopAnalysis } from "#lib/components/reports/loop-analysis/loop-analysis-context.svelte.js";
  import PageBodyLoading from "#lib/components/reports/loop-analysis/PageBodyLoading.svelte";
  import LoopHealthBody from "#lib/components/reports/loop-analysis/LoopHealthBody.svelte";

  const analysis = getLoopAnalysis();
  const resource = contextResource(() => getLoopHealth(analysis.query), { errorTitle: "Cannot load the loop health" });
</script>

{#if resource.current}
  <LoopHealthBody response={resource.current} />
{:else}
  <PageBodyLoading />
{/if}
