<!-- Stands in for layerchart's LineChart, which throws under vitest-browser-svelte: it prints how many rows it was given. -->
<script lang="ts">
  let { data }: { data?: unknown[] } = $props();
</script>

<span data-testid="chart-rows">{data?.length ?? 0}</span>
