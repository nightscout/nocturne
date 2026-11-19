<!--
  A block's rendered result. Every change runs the block's spec a moment later. Before the first
  result arrives the area is a quiet skeleton; after that the last result stays on screen, dimmed,
  with a small spinner, until the next one arrives.
-->
<script lang="ts">
  import type { Snippet } from "svelte";
  import { readReport, type ReportPanel } from "@nocturne/charts/core";
  import { untrack } from "svelte";
  import { Skeleton } from "#lib/components/ui/skeleton/index.js";
  import LoaderCircle from "@lucide/svelte/icons/loader-circle";
  import type { PreviewRunner } from "./preview-runner.svelte.js";
  import CustomReportRun from "../CustomReportRun.svelte";
  import type { BlockKind } from "./report.svelte.js";

  interface Props {
    spec: Record<string, unknown> | null;
    runner: PreviewRunner;
    kind: BlockKind;
    /** What the member does next before the block can run, or null. */
    incomplete?: string | null;
    /** Draw each chart's legend under its plot. */
    legend?: boolean;
    /** Draw each panel's figures; the workspace's group rows give an event chart's. */
    facts?: boolean;
    /** The workspace's words for a panel's heading, in place of the engine's title. */
    heading?: Snippet<[ReportPanel]>;
    /** Say, once each preview settles, that the block updated: only for the open block, and not a chart, whose view says it with its caption. */
    announce?: boolean;
  }

  let { spec, runner, kind, incomplete = null, legend = true, facts = true, heading, announce = false }: Props = $props();

  const UPDATED: Record<BlockKind, string> = {
    chart: "Chart updated.",
    figures: "Figures updated.",
    table: "Table updated.",
    comparison: "Comparison updated.",
  };
  const NOT_SHOWN: Record<BlockKind, string> = {
    chart: "The chart could not be shown.",
    figures: "The figures could not be shown.",
    table: "The table could not be shown.",
    comparison: "The comparison could not be shown.",
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
  /** A newer spec is on its way: not after a failed run, which shows its failure alone. */
  const stale = $derived(
    runner.running || (runner.failure === null && runner.ran !== null && runner.ran !== current)
  );
  /** The run came back but drew nothing: its spec was refused, or every panel failed or was not read. */
  const notShown = $derived.by(() => {
    const run = runner.run;
    if (!run) return false;
    if ((run.errors ?? []).length > 0) return true;
    const panels = readReport(run.result)?.panels ?? [];
    return panels.length === 0 || panels.every((p) => p.status !== "ok");
  });
  /** The result on screen is a refusal of settings since changed: it is hidden until the change is checked. */
  const staleRefusal = $derived(stale && (runner.run?.errors ?? []).length > 0);
  const settled = $derived(
    runner.ran === null || runner.running ? "" : runner.failure !== null || notShown ? NOT_SHOWN[kind] : UPDATED[kind]
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
  {#if staleRefusal}
    <div
      class="flex min-h-24 items-center justify-center gap-2 rounded-md border border-dashed border-border text-sm text-muted-foreground"
      aria-busy="true"
      data-testid="block-checking"
    >
      <LoaderCircle class="size-4 animate-spin motion-reduce:animate-none" aria-hidden="true" />
      Checking the change
    </div>
  {:else if runner.run}
    <div class="relative" aria-busy={stale}>
      <div
        class="transition-opacity duration-150 motion-reduce:transition-none"
        class:opacity-50={stale}
      >
        <CustomReportRun run={runner.run} {legend} {facts} {heading} headingLevel={3} />
      </div>
      {#if stale}
        <LoaderCircle
          class="absolute top-1 right-1 size-4 animate-spin text-muted-foreground motion-reduce:animate-none"
          aria-hidden="true"
        />
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
    </div>
  {/if}
{/if}
{#if announce}
  <p class="sr-only" aria-live="polite">{settled}</p>
{/if}
