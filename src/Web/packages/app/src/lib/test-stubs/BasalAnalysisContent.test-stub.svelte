<script lang="ts">
  const { range }: { range: { from: string; to: string } } = $props();
</script>

<div data-testid="basal-analysis" data-from={range.from} data-to={range.to}></div>
