<script lang="ts">
  import type { Snippet } from "svelte";
  import FirstReadingEmptyStateLoader from "./FirstReadingEmptyStateLoader.svelte";

  interface Props {
    /** The real glucose chart. This component owns the only instance of it. */
    chart: Snippet;
    /**
     * Whether the instance already has data on hand (server-loaded glucose or a
     * realtime value/history). When true the chart shows and the empty-state
     * check is skipped entirely, so a populated dashboard fires no status
     * query.
     */
    bypass: boolean;
    /** Passed through to the empty-state loader; see its docs. */
    recentHistoryReady: boolean;
    /** Passed through to the empty-state loader; see its docs. */
    hasRecentHistory: boolean;
  }

  let { chart, bypass, recentHistoryReady, hasRecentHistory }: Props = $props();

  let emptyStateShown = $state(false);

  // The chart is rendered once, here, so it is never destroyed and remounted as
  // the empty state comes and goes; it is only hidden behind the empty state.
  const chartHidden = $derived(!bypass && emptyStateShown);
</script>

<div hidden={chartHidden} aria-hidden={chartHidden}>
  {@render chart()}
</div>

<!--
  The loader owns the connector-status query, so keeping it unmounted while the
  instance already has data is what spares a populated dashboard that query.
-->
{#if !bypass}
  <FirstReadingEmptyStateLoader
    {recentHistoryReady}
    {hasRecentHistory}
    onResolve={(show) => (emptyStateShown = show)}
  />
{/if}
