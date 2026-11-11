<!--
  The lab's report: a title, a range and Save, then a stack of blocks. One block is open for
  editing; the others show only their result under a one-line header. A row of dashed buttons adds a
  block, each one disabled with its reason once the report is at a limit.
-->
<script lang="ts">
  import { onDestroy } from "svelte";
  import { runCustomReport } from "#lib/api/generated/customReports.generated.remote.js";
  import type { CustomReportCatalogue } from "#lib/api/generated/nocturne-api-client.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import * as Card from "#lib/components/ui/card/index.js";
  import * as Collapsible from "#lib/components/ui/collapsible/index.js";
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
  import { PreviewRunner } from "../builder/preview-runner.svelte.js";
  import BlockEditors from "./BlockEditors.svelte";
  import BlockPreview from "./BlockPreview.svelte";
  import { minuteLabel, spanLabel } from "./clock.js";
  import { describe } from "./describe.js";
  import RangeChoices from "./RangeChoices.svelte";
  import { Report, type Block } from "./report.svelte.js";
  import SaveControl from "./SaveControl.svelte";
  import type { BlockKind } from "./vocabulary.js";
  import WorkspaceView from "./WorkspaceView.svelte";
  import type { Starter } from "./workspace.svelte.js";

  interface Props {
    catalogue: CustomReportCatalogue;
    starter: Starter | null;
    onrestart: () => void;
  }

  let { catalogue, starter, onrestart }: Props = $props();

  // svelte-ignore state_referenced_locally
  const report = new Report(catalogue, starter, minuteLabel);
  report.dates = spanLabel;

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

  /**
   * The bottom of the report page's sticky header, its tab row included, so the
   * rail sticks below it. The layout's `--headroom-top` counts only the title bar.
   */
  let stickyTop = $state<number | null>(null);
  function measureHeader(node: HTMLElement) {
    const header = node
      .closest("[data-report-root]")
      ?.querySelector<HTMLElement>(":scope > .sticky");
    if (!header) return;
    const update = () => {
      stickyTop =
        (Number.parseFloat(getComputedStyle(header).top) || 0) +
        header.offsetHeight;
    };
    update();
    const observer = new ResizeObserver(update);
    observer.observe(header);
    return () => observer.disconnect();
  }

  function incompleteOf(block: Block): string | null {
    if (block.kind === "chart") return block.ws.incomplete;
    if (block.kind === "comparison" && !block.preset && !(block.a && block.b))
      return "Pick both periods.";
    return null;
  }
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

<div
  class="@container space-y-5"
  data-testid="lab-report"
  style:--lab-sticky-top={stickyTop === null ? undefined : `${stickyTop}px`}
  {@attach measureHeader}
>
  <div class="flex flex-wrap items-center justify-between gap-2">
    <p class="text-sm text-muted-foreground">
      Prototype. Save keeps the report; the lab itself keeps nothing.
    </p>
    <div class="flex items-center gap-2">
      <Button variant="ghost" size="sm" onclick={onrestart}>
        <RotateCcw aria-hidden="true" />
        Start again
      </Button>
      <SaveControl {report} />
    </div>
  </div>

  <div class="space-y-3">
    <Input
      variant="title"
      data-lab-title
      aria-label="Report title"
      placeholder="Untitled report"
      maxlength={catalogue.maxTitleLength}
      bind:value={() => report.title, (v: string) => (report.title = v)}
    />
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

  <ol class="space-y-4" data-testid="lab-blocks">
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
                  incomplete={incompleteOf(block)}
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
                incomplete={incompleteOf(block)}
              />
            {/if}
          </Card.Content>
        </Card.Root>
      </li>
    {/each}
  </ol>

  <div class="space-y-1.5">
    <div class="flex flex-wrap gap-2" data-testid="lab-add-blocks">
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
            All settings (prototype: read only)
          </Button>
        {/snippet}
      </Collapsible.Trigger>
      <Collapsible.Content>
        <pre class="mt-2 max-h-96 overflow-auto text-xs" data-testid="lab-spec">{JSON.stringify(
            report.spec(),
            null,
            2
          )}</pre>
      </Collapsible.Content>
    </Collapsible.Root>
  </div>
</div>

<style>
  /* Prototype only: the real build adds a named "selected" variant to the toggle component. Global, so
     items in portalled popovers take it too. */
  :global([data-lab-selected][data-state="on"]) {
    background-color: var(--primary);
    color: var(--primary-foreground);
  }

  /* Prototype only: the report header is translucent, so the canvas scrolling under it showed through
     in dark mode. The lab's header is opaque. */
  :global([data-report-root]:has([data-testid="lab-report"]) > .sticky) {
    background-color: var(--card);
    backdrop-filter: none;
  }

  /* Prototype only: the real build adds a size to the Input "title" variant. */
  :global(input[data-lab-title]) {
    font-size: 1.125rem;
    line-height: 1.75rem;
    font-weight: 600;
  }
</style>
