<script lang="ts">
  const {
    deferDrawing = false,
    showPredictions = false,
  }: { deferDrawing?: boolean; showPredictions?: boolean } = $props();
</script>

<div
  data-testid="glucose-chart"
  data-defer-drawing={deferDrawing}
  data-show-predictions={showPredictions}
>
  glucose chart
</div>
