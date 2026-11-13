<!--
  The report workspace: a title, a range and the page's save controls, then a stack of blocks. One
  block is open for editing; the others show only their result under a one-line header. A row of
  dashed buttons adds a block, each one disabled with its reason once the report is at a limit. "All
  settings" shows the spec the report makes, and can hand it to the form builder.
-->
<script lang="ts">
  import { onDestroy, type Snippet } from "svelte";
  import { runCustomReport } from "#lib/api/generated/customReports.generated.remote.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import * as Card from "#lib/components/ui/card/index.js";
  import * as Collapsible from "#lib/components/ui/collapsible/index.js";
  import { ConfirmDialog } from "#lib/components/ui/confirm-dialog/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import ArrowDown from "@lucide/svelte/icons/arrow-down";
  import ArrowUp from "@lucide/svelte/icons/arrow-up";
  import ChartLine from "@lucide/svelte/icons/chart-line";
  import ChevronRight from "@lucide/svelte/icons/chevron-right";
  import Columns2 from "@lucide/svelte/icons/columns-2";
  import Hash from "@lucide/svelte/icons/hash";
  import Pencil from "@lucide/svelte/icons/pencil";
  import SlidersHorizontal from "@lucide/svelte/icons/sliders-horizontal";
  import Plus from "@lucide/svelte/icons/plus";
  import RotateCcw from "@lucide/svelte/icons/rotate-ccw";
  import Table from "@lucide/svelte/icons/table";
  import Trash2 from "@lucide/svelte/icons/trash-2";
  import { PreviewRunner } from "../builder/preview-runner.svelte.js";
  import BlockEditors from "./BlockEditors.svelte";
  import BlockPreview from "./BlockPreview.svelte";
  import { describe } from "./describe.js";
  import RangeChoices from "./RangeChoices.svelte";
  import type { Block, Report } from "./report.svelte.js";
  import type { BlockKind } from "./report.svelte.js";
  import WorkspaceView from "./WorkspaceView.svelte";

  interface Props {
    report: Report;
    /**
     * The page's save controls, beside the report's title, given why the engine refuses a block's
     * settings in its last preview, or null.
     */
    actions: Snippet<[string | null]>;
    /** Goes back to the start screen; a saved report has none. */
    onrestart?: () => void;
    /** Opens the report's spec in the form builder, once the member has confirmed. */
    onform: (spec: Record<string, unknown>) => void;
  }

  let { report, actions, onrestart, onform }: Props = $props();

  const catalogue = $derived(report.catalogue);
  let confirmingForm = $state(false);
  let confirmingRestart = $state(false);

  /** One preview runner for each block, made the first time the block draws. */
  const runners: Record<number, PreviewRunner> = {};
  function runnerOf(key: number): PreviewRunner {
    return (runners[key] ??= new PreviewRunner((spec) =>
      runCustomReport({ spec, preview: true })
    ));
  }
  onDestroy(() => {
    for (const r of Object.values(runners)) r.dispose();
  });

  /** Why the report cannot be saved as its previews stand: a block whose settings the engine refused. */
  function refused(): string | null {
    const at = report.blocks.findIndex((b) => (runners[b.key]?.run?.errors ?? []).length > 0);
    return at < 0 ? null : `Change block ${at + 1} first. The report engine refuses one of its settings.`;
  }

  function remove(key: number) {
    runners[key]?.dispose();
    delete runners[key];
    report.remove(key);
  }

  const KIND_ICONS: Record<BlockKind, typeof ChartLine> = {
    chart: ChartLine,
    figures: Hash,
    table: Table,
    comparison: Columns2,
  };
  const KIND_NAMES: Record<BlockKind, string> = {
    chart: "Chart",
    figures: "Figures",
    table: "Table",
    comparison: "Comparison",
  };
  const ADDS: { kind: BlockKind; label: string }[] = [
    { kind: "chart", label: "Add a chart" },
    { kind: "figures", label: "Add figures" },
    { kind: "table", label: "Add a table" },
    { kind: "comparison", label: "Add a comparison" },
  ];

  const adds = $derived(
    ADDS.map((a) => ({ ...a, reason: report.addReason(a.kind) }))
  );
  const addReasons = $derived.by(() => {
    const out: { reason: string; labels: string[] }[] = [];
    for (const a of adds) {
      if (!a.reason) continue;
      const found = out.find((r) => r.reason === a.reason);
      if (found) found.labels.push(a.label);
      else out.push({ reason: a.reason, labels: [a.label] });
    }
    return out;
  });

  const rangeId = $derived(
    report.ranges.find((r) => r.hours === report.rangeHours)?.id ?? ""
  );

  /** The collapsed comparisons shown in full. */
  let expanded = $state<number[]>([]);

</script>

{#snippet header(block: Block, index: number, open: boolean)}
  {@const Icon = KIND_ICONS[block.kind]}
  {@const title = report.blockTitle(block).trim()}
  <div class="flex flex-wrap items-center gap-x-3 gap-y-1">
    <Icon class="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
    <h3 class="min-w-0 text-sm font-medium">
      {title || KIND_NAMES[block.kind]}
    </h3>
    <p class="min-w-0 flex-1 truncate text-sm text-muted-foreground">
      {#if !open || block.kind !== "chart"}{report.caption(block)}{/if}
    </p>
    <div class="flex flex-wrap items-center gap-1">
      {#if !open}
        <Button
          variant="ghost"
          size="xs"
          onclick={() => report.openBlock(block.key)}
          data-testid="block-edit"
        >
          <Pencil aria-hidden="true" />
          Edit
        </Button>
      {/if}
      {#if index > 0}
        <Button variant="ghost" size="xs" onclick={() => report.move(block.key, -1)}>
          <ArrowUp aria-hidden="true" />
          Move up
        </Button>
      {/if}
      {#if index < report.blocks.length - 1}
        <Button variant="ghost" size="xs" onclick={() => report.move(block.key, 1)}>
          <ArrowDown aria-hidden="true" />
          Move down
        </Button>
      {/if}
      <Button
        variant="ghost-destructive"
        size="xs"
        onclick={() => remove(block.key)}
      >
        <Trash2 aria-hidden="true" />
        Remove
      </Button>
    </div>
  </div>
{/snippet}

<div class="@container space-y-5" data-testid="report-workspace">
  <div class="space-y-3">
    <div class="flex flex-wrap items-center justify-between gap-2">
      <Input
        variant="title"
        size="heading"
        class="min-w-0 flex-1 basis-64"
        aria-label="Report title"
        placeholder="Untitled report"
        maxlength={catalogue.maxTitleLength}
        bind:value={() => report.title, (v: string) => (report.title = v)}
      />
      <div class="flex flex-wrap items-center gap-2">
        {#if onrestart}
          <Button variant="ghost" size="sm" onclick={() => (confirmingRestart = true)}>
            <RotateCcw aria-hidden="true" />
            Start again
          </Button>
        {/if}
        {@render actions(refused())}
      </div>
    </div>
    <RangeChoices
      id="report-range"
      label="Range of the report"
      options={report.ranges}
      value={rangeId}
      onchange={(id) => {
        const found = report.ranges.find((r) => r.id === id);
        if (found) report.setRange(found.hours);
      }}
    />
  </div>

  <ol class="space-y-4" data-testid="report-blocks">
    {#each report.blocks as block, index (block.key)}
      {@const open = report.open === block.key}
      <li data-block-kind={block.kind} data-open={open}>
        <Card.Root class="min-w-0">
          <Card.Content class="space-y-4">
            {@render header(block, index, open)}
            {#if open}
              {#if block.kind === "chart"}
                <WorkspaceView {report} {block} runner={runnerOf(block.key)} />
              {:else}
                <BlockEditors {report} {block} runner={runnerOf(block.key)} />
              {/if}
            {:else if block.kind === "comparison"}
              <div
                class={[
                  "relative",
                  !expanded.includes(block.key) && "max-h-[26rem] overflow-hidden",
                ]}
                data-testid="comparison-clamp"
              >
                <BlockPreview
                  spec={report.previewSpec(block)}
                  runner={runnerOf(block.key)}
                  kind={block.kind}
                  incomplete={report.incomplete(block)}
                />
                {#if !expanded.includes(block.key)}
                  <div
                    class="pointer-events-none absolute inset-x-0 bottom-0 h-16 bg-linear-to-t from-card to-transparent"
                  ></div>
                {/if}
              </div>
              <Button
                variant="subtle"
                size="xs"
                class="group"
                aria-expanded={expanded.includes(block.key)}
                onclick={() =>
                  (expanded = expanded.includes(block.key)
                    ? expanded.filter((k) => k !== block.key)
                    : [...expanded, block.key])}
              >
                <ChevronRight
                  class="transition-transform group-aria-expanded:rotate-90 motion-reduce:transition-none"
                  aria-hidden="true"
                />
                {expanded.includes(block.key) ? "Show fewer figures" : "Show all figures"}
              </Button>
            {:else}
              <BlockPreview
                spec={report.previewSpec(block)}
                runner={runnerOf(block.key)}
                kind={block.kind}
                incomplete={report.incomplete(block)}
              />
            {/if}
          </Card.Content>
        </Card.Root>
      </li>
    {/each}
  </ol>

  <div class="space-y-1.5">
    <div class="flex flex-wrap gap-2" data-testid="add-blocks">
      {#each adds as a (a.kind)}
        <Button
          variant="dashed"
          size="sm"
          disabled={a.reason !== null}
          {...describe(a.reason)}
          onclick={() => report.add(a.kind)}
        >
          <Plus aria-hidden="true" />
          {a.label}
        </Button>
      {/each}
    </div>
    {#each addReasons as r (r.reason)}
      <p class="text-xs text-muted-foreground">
        {r.labels.join(", ")}: {r.reason}
      </p>
    {/each}
  </div>

  <div class="border-t border-border pt-3">
    <Collapsible.Root>
      <Collapsible.Trigger>
        {#snippet child({ props }: { props: Record<string, unknown> })}
          <Button {...props} variant="subtle" size="sm" class="group">
            <ChevronRight
              class="transition-transform group-data-[state=open]:rotate-90 motion-reduce:transition-none"
              aria-hidden="true"
            />
            All settings
          </Button>
        {/snippet}
      </Collapsible.Trigger>
      <Collapsible.Content class="space-y-3 pt-2">
        <p class="text-xs text-muted-foreground">The report as the engine reads it. Change it in the form to edit each setting.</p>
        <pre class="max-h-96 overflow-auto rounded-md border border-border bg-muted/30 p-3 text-xs" data-testid="report-spec">{JSON.stringify(
            report.spec(),
            null,
            2
          )}</pre>
        <Button variant="outline" size="sm" onclick={() => (confirmingForm = true)} data-testid="change-in-form">
          <SlidersHorizontal aria-hidden="true" />
          Change in the form
        </Button>
      </Collapsible.Content>
    </Collapsible.Root>
  </div>
</div>

<ConfirmDialog
  open={confirmingForm}
  onOpenChange={(open) => (confirmingForm = open)}
  title="Change this report in the form?"
  confirmLabel="Change in the form"
  onConfirm={() => {
    confirmingForm = false;
    onform(report.spec());
  }}
>
  {#snippet description()}
    The form shows each setting of the report. After you change the report in the form, it cannot
    open in this builder again.
  {/snippet}
</ConfirmDialog>

<ConfirmDialog
  open={confirmingRestart}
  onOpenChange={(open) => (confirmingRestart = open)}
  title="Start again?"
  confirmLabel="Start again"
  destructive
  onConfirm={() => {
    confirmingRestart = false;
    onrestart?.();
  }}
>
  {#snippet description()}
    This report is not saved. If you start again, Nocturne discards it.
  {/snippet}
</ConfirmDialog>
