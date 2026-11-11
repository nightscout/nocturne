<!--
  The open block of figures, a table or a comparison: its title and range, what it reads, and what
  it shows, above its rendered result. A choice that cannot be made now is disabled, with its reason
  beside it.
-->
<script lang="ts">
  import { Checkbox } from "#lib/components/ui/checkbox/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import * as Collapsible from "#lib/components/ui/collapsible/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import * as Select from "#lib/components/ui/select/index.js";
  import * as ToggleGroup from "#lib/components/ui/toggle-group/index.js";
  import ChevronRight from "@lucide/svelte/icons/chevron-right";
  import type { PreviewRunner } from "../builder/preview-runner.svelte.js";
  import BlockPreview from "./BlockPreview.svelte";
  import DateSpanField from "./DateSpanField.svelte";
  import EventGroups from "./EventGroups.svelte";
  import RangeChoices from "./RangeChoices.svelte";
  import { EVENT_DATASET } from "./vocabulary.js";
  import type {
    ComparisonBlock,
    FiguresBlock,
    Report,
    TableBlock,
  } from "./report.svelte.js";

  interface Props {
    report: Report;
    block: FiguresBlock | TableBlock | ComparisonBlock;
    runner: PreviewRunner;
  }

  let { report, block, runner }: Props = $props();

  const PLACEHOLDERS = {
    figures: "Untitled figures",
    table: "Untitled table",
    comparison: "Untitled comparison",
  };
</script>

{#snippet columns(target: FiguresBlock | TableBlock, heading: string)}
  <fieldset class="space-y-2">
    <legend class="text-xs text-muted-foreground">{heading}</legend>
    <ul class="grid gap-x-4 gap-y-1.5 @md:grid-cols-2 @3xl:grid-cols-3">
      {#each report.columns(target) as column (column.field)}
        <li class="flex items-start gap-2">
          <Checkbox
            id="col-{target.key}-{column.field}"
            checked={column.on}
            disabled={column.reason !== null}
            aria-describedby={column.reason
              ? `col-why-${target.key}-${column.field}`
              : undefined}
            onCheckedChange={() => report.toggleColumn(target, column.field)}
          />
          <div>
            <Label for="col-{target.key}-{column.field}" variant="option">
              {column.label}
            </Label>
            {#if column.reason}
              <p
                id="col-why-{target.key}-{column.field}"
                class="text-xs text-muted-foreground"
              >
                {column.reason}
              </p>
            {/if}
          </div>
        </li>
      {/each}
    </ul>
  </fieldset>
{/snippet}

<div class="space-y-5" data-testid="block-editor-{block.kind}">
  <div class="space-y-3">
    <Input
      variant="title"
      size="heading"
      aria-label="Title"
      placeholder={PLACEHOLDERS[block.kind]}
      maxlength={report.catalogue.maxTitleLength}
      bind:value={
        () => report.blockTitle(block),
        (v: string) => report.setBlockTitle(block, v)
      }
    />
    {#if block.kind !== "comparison"}
      <RangeChoices
        id="block-range-{block.key}"
        label="Range of this block"
        options={report.blockRanges(block)}
        value={report.blockRangeId(block)}
        onchange={(id) => {
          const option = report.blockRanges(block).find((o) => o.id === id);
          if (option) report.setBlockRange(block, option.hours);
        }}
      />
    {/if}
  </div>

  {#if block.kind === "figures"}
    {@const sources = report.figureSources.map((s) => ({
      ...s,
      reason:
        s.dataset === block.source ? null : report.sourceReason(block, s.dataset),
    }))}
    <div class="space-y-1.5">
      <p class="text-xs text-muted-foreground" id="figures-source-{block.key}">
        Figures from
      </p>
      <ToggleGroup.Root
        type="single"
        variant="choice"
        size="sm"
        spacing={1}
        class="flex-wrap"
        aria-labelledby="figures-source-{block.key}"
        bind:value={
          () => block.source,
          (v: string) => v && report.setSource(block, v)
        }
      >
        {#each sources as s (s.dataset)}
          <ToggleGroup.Item
            value={s.dataset}
            disabled={s.reason !== null}
          >
            {s.label}
          </ToggleGroup.Item>
        {/each}
      </ToggleGroup.Root>
      {#each sources.filter((s) => s.reason) as s (s.dataset)}
        <p class="text-xs text-muted-foreground">{s.label}: {s.reason}</p>
      {/each}
    </div>
    {@render columns(block, "Figures to show")}
  {:else if block.kind === "table"}
    {@const sources = report.tableSources.map((s) => ({
      ...s,
      reason:
        s.dataset === block.source ? null : report.sourceReason(block, s.dataset),
    }))}
    <div class="space-y-1.5">
      <Label for="table-source-{block.key}">One row for</Label>
      <Select.Root
        type="single"
        bind:value={
          () => block.source,
          (v: string) => v && report.setSource(block, v)
        }
      >
        <Select.Trigger id="table-source-{block.key}" size="sm" class="w-72">
          {report.sourceLabel(block)}
        </Select.Trigger>
        <Select.Content>
          {#each sources as s (s.dataset)}
            <Select.Item
              value={s.dataset}
              label={s.label}
              disabled={s.reason !== null}
            >
              {s.label}
            </Select.Item>
          {/each}
        </Select.Content>
      </Select.Root>
    </div>
    {#if block.source === EVENT_DATASET}
      <EventGroups ws={block.events} report={null} />
    {/if}
    <Collapsible.Root>
      <Collapsible.Trigger>
        {#snippet child({ props }: { props: Record<string, unknown> })}
          <Button {...props} variant="subtle" size="xs" class="group">
            <ChevronRight
              class="transition-transform group-data-[state=open]:rotate-90 motion-reduce:transition-none"
              aria-hidden="true"
            />
            Columns ({block.columns.length} of {report.columns(block).length})
          </Button>
        {/snippet}
      </Collapsible.Trigger>
      <Collapsible.Content class="pt-2">
        {@render columns(block, "Columns to show")}
      </Collapsible.Content>
    </Collapsible.Root>
  {:else}
    <div class="space-y-1.5">
      <p class="text-xs text-muted-foreground" id="comparison-preset-{block.key}">
        Compare
      </p>
      <ToggleGroup.Root
        type="single"
        variant="choice"
        size="sm"
        spacing={1}
        class="flex-wrap"
        aria-labelledby="comparison-preset-{block.key}"
        bind:value={
          () => block.preset ?? "",
          (v: string) => v && report.setComparisonPreset(block, v)
        }
      >
        {#each report.comparisonPresets as p (p.value)}
          <ToggleGroup.Item value={p.value}>
            {p.label}
          </ToggleGroup.Item>
        {/each}
      </ToggleGroup.Root>
    </div>
    <Collapsible.Root open={block.preset === null}>
      <Collapsible.Trigger>
        {#snippet child({ props }: { props: Record<string, unknown> })}
          <Button {...props} variant="subtle" size="xs" class="group">
            <ChevronRight
              class="transition-transform group-data-[state=open]:rotate-90 motion-reduce:transition-none"
              aria-hidden="true"
            />
            More
          </Button>
        {/snippet}
      </Collapsible.Trigger>
      <Collapsible.Content class="space-y-2 pt-2">
        <p class="text-xs text-muted-foreground">Two periods of your own</p>
        <div class="flex flex-wrap items-center gap-2">
          <span class="text-xs text-muted-foreground">Period A</span>
          <DateSpanField
            value={block.a}
            label="Period A"
            placeholder="Pick period A"
            onchange={(span) => report.setComparisonPeriods(block, span, block.b)}
          />
          <span class="text-xs text-muted-foreground">Period B</span>
          <DateSpanField
            value={block.b}
            label="Period B"
            placeholder="Pick period B"
            onchange={(span) => report.setComparisonPeriods(block, block.a, span)}
          />
        </div>
      </Collapsible.Content>
    </Collapsible.Root>
  {/if}

  <BlockPreview
    spec={report.previewSpec(block)}
    {runner}
    kind={block.kind}
    incomplete={block.kind === "comparison" && !block.preset && !(block.a && block.b)
      ? "Pick both periods."
      : null}
  />
</div>
