<script lang="ts">
  import FirstReadingChartArea from "./FirstReadingChartArea.svelte";

  interface Props {
    bypass: boolean;
    recentHistoryReady: boolean;
    hasRecentHistory: boolean;
  }

  let { bypass, recentHistoryReady, hasRecentHistory }: Props = $props();
</script>

<FirstReadingChartArea {bypass} {recentHistoryReady} {hasRecentHistory}>
  {#snippet chart(chartVisible)}
    <div>
      CHART SHOWN
      <span data-testid="coach-eligible">{chartVisible ? "yes" : "no"}</span>
    </div>
  {/snippet}
</FirstReadingChartArea>
