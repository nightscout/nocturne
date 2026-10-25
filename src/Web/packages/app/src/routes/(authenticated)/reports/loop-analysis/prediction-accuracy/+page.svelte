<script lang="ts">
  import { getPredictionAccuracy } from "$api/generated/loopAnalysis.generated.remote";
  import { contextResource } from "$lib/hooks/resource-context.svelte";
  import { getLoopAnalysis } from "$lib/components/reports/loop-analysis/loop-analysis-context.svelte";
  import PageBodyLoading from "$lib/components/reports/loop-analysis/PageBodyLoading.svelte";
  import PredictionAccuracyBody from "$lib/components/reports/loop-analysis/PredictionAccuracyBody.svelte";

  const analysis = getLoopAnalysis();
  const resource = contextResource(() => getPredictionAccuracy(analysis.query), { errorTitle: "Cannot load the prediction accuracy" });
</script>

{#if resource.current}
  <PredictionAccuracyBody response={resource.current} />
{:else}
  <PageBodyLoading />
{/if}
