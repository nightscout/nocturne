<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import VisibleChart from "./VisibleChart.svelte";
  let label = $state("Original data");
</script>

<Button onclick={() => (label = "Updated data")}>Update data</Button>
<div class="h-60">
  <VisibleChart label="Year chart">
    <div data-testid="chart">{label}</div>
  </VisibleChart>
</div>
