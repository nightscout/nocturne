<!--
  A block's rendered result. Every change runs the block's spec a moment later. Before the first
  result arrives the area is a quiet skeleton; after that the last result stays on screen, dimmed,
  with a small spinner, until the next one arrives.
-->
<script lang="ts">
  import { untrack } from "svelte";
  import { Skeleton } from "#lib/components/ui/skeleton/index.js";
  import LoaderCircle from "@lucide/svelte/icons/loader-circle";
  import type { PreviewRunner } from "../builder/preview-runner.svelte.js";
  import CustomReportRun from "../CustomReportRun.svelte";
  import type { BlockKind } from "./vocabulary.js";

  interface Props {
    spec: Record<string, unknown> | null;
    runner: PreviewRunner;
    kind: BlockKind;
    /** What the member does next before the block can run, or null. */
    incomplete?: string | null;
  }

  let { spec, runner, kind, incomplete = null }: Props = $props();

  const LOADING: Record<BlockKind, string> = {
    chart: "Loading the chart",
    figures: "Loading the figures",
    table: "Loading the table",
    comparison: "Loading the comparison",
  };
  const UPDATING: Record<BlockKind, string> = {
    chart: "Updating the chart",
    figures: "Updating the figures",
    table: "Updating the table",
    comparison: "Updating the comparison",
  };

  $effect(() => {
    const next = spec;
    if (!next) return;
    untrack(() => {
      if (runner.run === null && !runner.running) runner.now(next);
      else runner.schedule(next);
    });
  });

  const current = $derived(JSON.stringify(spec));
  const stale = $derived(
    runner.running || (runner.ran !== null && runner.ran !== current)
  );
</script>

{#if incomplete || !spec}
  <div
    class="flex min-h-48 items-center justify-center rounded-md border border-dashed border-border text-sm text-muted-foreground"
  >
    {incomplete}
  </div>
{:else}
  {#if runner.failure}
    <p class="mb-2 text-sm text-destructive" role="alert">
      The preview could not be made. Nocturne will try again when you make a
      change.
    </p>
  {/if}
  {#if runner.run}
    <div class="relative" aria-busy={stale}>
      <div
        class="transition-opacity duration-150 motion-reduce:transition-none"
        class:opacity-50={stale}
      >
        <CustomReportRun run={runner.run} />
      </div>
      {#if stale}
        <LoaderCircle
          class="absolute top-1 right-1 size-4 animate-spin text-muted-foreground motion-reduce:animate-none"
          aria-hidden="true"
        />
        <span class="sr-only" role="status">{UPDATING[kind]}</span>
      {/if}
    </div>
  {:else if !runner.failure}
    <div
      class="space-y-3"
      aria-busy="true"
      data-testid="block-loading"
    >
      <Skeleton class="h-4 w-1/3" />
      <Skeleton class={kind === "chart" ? "h-80 w-full" : "h-32 w-full"} />
      <span class="sr-only" role="status">{LOADING[kind]}</span>
    </div>
  {/if}
{/if}
