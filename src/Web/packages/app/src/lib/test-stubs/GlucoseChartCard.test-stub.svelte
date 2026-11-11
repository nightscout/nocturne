<script lang="ts">
  const {
    deferDrawing = false,
    showPredictions = false,
    dateRange,
  }: {
    deferDrawing?: boolean;
    showPredictions?: boolean;
    dateRange?: { from: Date | string; to: Date | string };
  } = $props();
</script>

<div
  data-testid="glucose-chart"
  data-defer-drawing={deferDrawing}
  data-show-predictions={showPredictions}
  data-from={dateRange?.from}
  data-to={dateRange?.to}
>
  glucose chart
</div>
