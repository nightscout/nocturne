<script lang="ts">
  const { deferDrawing = false }: { deferDrawing?: boolean } = $props();
</script>

<div data-testid="glucose-chart" data-defer-drawing={deferDrawing}>glucose chart</div>
