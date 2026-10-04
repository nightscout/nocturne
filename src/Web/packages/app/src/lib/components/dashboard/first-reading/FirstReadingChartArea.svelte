<script lang="ts">
  import type { Snippet } from "svelte";
  import { fade } from "svelte/transition";
  import { Artwork, prefersReducedMotion } from "@nocturne/watercolour";
  import FirstReadingEmptyStateLoader from "./FirstReadingEmptyStateLoader.svelte";

  interface Props {
    /**
     * The real glucose chart. This component owns the only instance of it. The
     * boolean argument is whether the chart is currently shown (not hidden
     * behind the empty state), so the caller can gate a coach mark on it — a
     * mark attached to a hidden element positions against a zero rect.
     */
    chart: Snippet<[boolean]>;
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

  /** Short enough that the chart is readable within a second of the reading. */
  const ARRIVAL_FADE_MS = 700;

  let emptyStateShown = $state(false);

  // The chart is rendered once, here, so it is never destroyed and remounted as
  // the empty state comes and goes; it is only hidden behind the empty state.
  const chartHidden = $derived(!bypass && emptyStateShown);

  // Data turning up while the empty state is on screen is the tenant's first
  // reading ever: the empty state only shows when none has arrived, and the
  // loader unmounts with `bypass`, so nothing resets `emptyStateShown`.
  const firstReadingArrived = $derived(bypass && emptyStateShown);
  // The sunrise mounts as the reading lands and is released straight away, so
  // all it plays is its fade off the chart already in place beneath it.
  let arrivalShown = $state(true);
  $effect(() => {
    if (firstReadingArrived) arrivalShown = false;
  });
</script>

<div class="relative" hidden={chartHidden} aria-hidden={chartHidden}>
  {@render chart(!chartHidden)}

  {#if firstReadingArrived && arrivalShown && !prefersReducedMotion()}
    <div
      data-testid="first-reading-arrival"
      aria-hidden="true"
      class="pointer-events-none absolute inset-0 grid place-items-center rounded-xl border bg-card"
      out:fade={{ duration: ARRIVAL_FADE_MS }}
    >
      <Artwork
        artwork="sunrise"
        palette="ember"
        motion="reduced"
        class="size-full max-h-80 max-w-80"
      />
    </div>
  {/if}
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
