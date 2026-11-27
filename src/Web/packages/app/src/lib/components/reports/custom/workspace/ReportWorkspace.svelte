<!--
  The report workspace: one row of the report's title, its range and the page's save controls, then
  a stack of blocks. One block is open for editing; the others show only their result under a
  one-line header (an open block other than a chart of signals names only its kind there: its
  preview carries its title). A row of dashed buttons adds a block, each one disabled with its reason once the
  report is at a limit.
-->
<script lang="ts">
  import { formatList } from "#lib/utils/formatting.js";
  import { onDestroy, type Snippet } from "svelte";
  import { runCustomReport } from "#lib/api/generated/customReports.generated.remote.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import * as Card from "#lib/components/ui/card/index.js";
  import { ConfirmDialog } from "#lib/components/ui/confirm-dialog/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import ArrowDown from "@lucide/svelte/icons/arrow-down";
  import ArrowUp from "@lucide/svelte/icons/arrow-up";
  import ChartLine from "@lucide/svelte/icons/chart-line";
  import ChevronRight from "@lucide/svelte/icons/chevron-right";
  import Columns2 from "@lucide/svelte/icons/columns-2";
  import Hash from "@lucide/svelte/icons/hash";
  import Pencil from "@lucide/svelte/icons/pencil";
  import Plus from "@lucide/svelte/icons/plus";
  import RotateCcw from "@lucide/svelte/icons/rotate-ccw";
  import Table from "@lucide/svelte/icons/table";
  import Trash2 from "@lucide/svelte/icons/trash-2";
  import { PreviewRunner } from "./preview-runner.svelte.js";
  import BlockEditors from "./BlockEditors.svelte";
  import BlockPreview from "./BlockPreview.svelte";
  import { describe } from "./describe.js";
  import { dismissUndos, offerUndo } from "./undo.svelte.js";
  import GivenBlockView from "./GivenBlockView.svelte";
  import RangeChoices from "./RangeChoices.svelte";
  import { AS_SAVED, shownKind, type Block, type BlockKind, type Report } from "./report.svelte.js";
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
  }

  let { report, actions, onrestart }: Props = $props();

  const catalogue = $derived(report.catalogue);
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
    dismissUndos();
  });

  /** Why the report cannot be saved as its previews stand: a block whose current settings the engine refused, named by its title. */
  function refused(): string | null {
    const block = report.blocks.find((b) => (runners[b.key]?.refusalOf(report.previewSpec(b)) ?? []).length > 0);
    if (!block) return null;
    const name = report.blockTitle(block).trim();
    return name
      ? `Change "${name}" first. The report engine cannot run its settings.`
      : KIND_CHANGE_FIRST[shownKind(block)];
  }

  /** Removes a block, with a toast that can put it back where it was. */
  function remove(key: number) {
    const at = report.blocks.findIndex((b) => b.key === key);
    const block = report.blocks[at];
    if (!block) return;
    const name = report.blockTitle(block).trim();
    runners[key]?.dispose();
    delete runners[key];
    report.remove(key);
    offerUndo(
      name ? `Nocturne removed "${name}".` : KIND_REMOVED[shownKind(block)],
      () => report.restoreBlock(block, at)
    );
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
  const KIND_REMOVED: Record<BlockKind, string> = {
    chart: "Nocturne removed the chart.",
    figures: "Nocturne removed the figures.",
    table: "Nocturne removed the table.",
    comparison: "Nocturne removed the comparison.",
  };
  const KIND_CHANGE_FIRST: Record<BlockKind, string> = {
    chart: "Change the chart first. The report engine cannot run its settings.",
    figures: "Change the figures first. The report engine cannot run its settings.",
    table: "Change the table first. The report engine cannot run its settings.",
    comparison: "Change the comparison first. The report engine cannot run its settings.",
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

  /** The report's range choices, led by the range the spec gave when no preset states it. */
  const rangeOptions = $derived([
    ...(report.pinnedLabel ? [{ id: AS_SAVED, label: report.pinnedLabel, hours: report.rangeHours, reason: null }] : []),
    ...report.ranges,
  ]);
  const rangeId = $derived(
    report.pinnedLabel ? AS_SAVED : (report.ranges.find((r) => r.hours === report.rangeHours)?.id ?? "")
  );

  /** The collapsed comparisons shown in full. */
  let expanded = $state<number[]>([]);
</script>

{#snippet header(block: Block, index: number, open: boolean)}
  {@const kind = shownKind(block)}
  {@const Icon = KIND_ICONS[kind]}
  {@const title = open && block.kind !== "chart" ? "" : report.blockTitle(block).trim()}
  <div class="flex flex-wrap items-center gap-x-3 gap-y-1">
    <Icon class="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
    <h2 class="min-w-0 text-sm font-medium">
      {title || KIND_NAMES[kind]}
    </h2>
    <p class="min-w-0 flex-1 truncate text-sm text-muted-foreground">
      {#if !open}{report.caption(block)}{/if}
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

<div class="@container space-y-4" data-testid="report-workspace">
  <div class="flex flex-wrap items-start gap-x-4 gap-y-2">
    <Input
      variant="title"
      size="heading"
      class="min-w-0 flex-1 basis-56"
      aria-label="Report title"
      placeholder="Untitled report"
      maxlength={catalogue.maxTitleLength}
      bind:value={() => report.title, (v: string) => (report.title = v)}
    />
    <div class="order-last basis-full @5xl:order-none @5xl:basis-auto">
      <RangeChoices
        id="report-range"
        label="Range of the report"
        options={rangeOptions}
        value={rangeId}
        onchange={(id) => {
          const found = report.ranges.find((r) => r.id === id);
          if (found) report.setRange(found.hours);
        }}
      />
    </div>
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

  <ol class="space-y-4" data-testid="report-blocks">
    {#each report.blocks as block, index (block.key)}
      {@const open = report.open === block.key}
      <li data-block-kind={shownKind(block)} data-open={open}>
        <Card.Root size="sm" class="min-w-0">
          <Card.Content class="space-y-3 px-0">
            {@render header(block, index, open)}
            {#if open && report.notice?.key === block.key}
              <p class="text-xs text-muted-foreground" role="status" data-testid="block-notice">{report.notice.text}</p>
            {/if}
            {#if open}
              {#if block.kind === "chart"}
                <WorkspaceView {report} {block} runner={runnerOf(block.key)} />
              {:else if block.kind === "given"}
                <GivenBlockView {report} {block} runner={runnerOf(block.key)} />
              {:else}
                <BlockEditors {report} {block} runner={runnerOf(block.key)} />
              {/if}
            {:else if shownKind(block) === "comparison"}
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
                  kind="comparison"
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
                kind={shownKind(block)}
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
        {formatList(r.labels, "unit")}: {r.reason}
      </p>
    {/each}
  </div>
</div>

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
