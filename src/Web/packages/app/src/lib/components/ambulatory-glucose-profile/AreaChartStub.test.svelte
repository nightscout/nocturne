<script lang="ts">
  let {
    yDomain,
    annotations,
  }: {
    yDomain: [number, number];
    annotations: Array<{ label?: string }>;
  } = $props();
</script>

<div data-testid="agp-chart" data-y-max={yDomain[1]}>
  {#each annotations as annotation, i (i)}
    {#if annotation.label}<span>{annotation.label}</span>{/if}
  {/each}
</div>
