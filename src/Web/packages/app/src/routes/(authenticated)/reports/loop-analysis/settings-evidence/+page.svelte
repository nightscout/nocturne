<script lang="ts">
  import { getSettingsEvidence } from "$api/generated/loopAnalysis.generated.remote";
  import { contextResource } from "$lib/hooks/resource-context.svelte";
  import { getLoopAnalysis } from "$lib/components/reports/loop-analysis/loop-analysis-context.svelte";
  import PageBodyLoading from "$lib/components/reports/loop-analysis/PageBodyLoading.svelte";
  import SettingsEvidenceBody from "$lib/components/reports/loop-analysis/SettingsEvidenceBody.svelte";

  const analysis = getLoopAnalysis();
  const resource = contextResource(() => getSettingsEvidence(analysis.query), { errorTitle: "Cannot load the settings evidence" });
</script>

{#if resource.current}
  <SettingsEvidenceBody response={resource.current} sensitivityHref={analysis.path("loop-health", "sensitivity")} />
{:else}
  <PageBodyLoading />
{/if}
