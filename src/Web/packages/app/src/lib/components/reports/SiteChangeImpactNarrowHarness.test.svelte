<script lang="ts">
  import type { SiteChangeImpactAnalysis } from "$lib/api";
  import SiteChangeImpactChart from "./SiteChangeImpactChart.svelte";

  let { analysis, width }: { analysis: SiteChangeImpactAnalysis; width: number } = $props();
</script>

<div class="w-(--harness-w)" style:--harness-w="{width}px">
  <SiteChangeImpactChart {analysis} />
</div>
