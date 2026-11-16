<!--
  An open block held as the spec gave it, result first: its preview in the main column; beside it on
  a wide block (under it on a narrow one) what it shows and how to change it. A chart lists its
  signals, adds one from the catalogue (only the signals along its axis), and keeps the rest in its
  advanced settings; figures and a table list their columns; a comparison its two periods.
-->
<script lang="ts">
  import { Button } from "#lib/components/ui/button/index.js";
  import { Checkbox } from "#lib/components/ui/checkbox/index.js";
  import * as Collapsible from "#lib/components/ui/collapsible/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import * as ToggleGroup from "#lib/components/ui/toggle-group/index.js";
  import ChevronRight from "@lucide/svelte/icons/chevron-right";
  import Plus from "@lucide/svelte/icons/plus";
  import X from "@lucide/svelte/icons/x";
  import BlockPreview from "./BlockPreview.svelte";
  import BlockTitleRange from "./BlockTitleRange.svelte";
  import DateSpanField from "./DateSpanField.svelte";
  import { givenSource } from "./catalogue-source.js";
  import { describe } from "./describe.js";
  import GivenChartAdvanced from "./GivenChartAdvanced.svelte";
  import { givenKind } from "./given.svelte.js";
  import type { PreviewRunner } from "./preview-runner.svelte.js";
  import type { GivenBlock, Report } from "./report.svelte.js";
  import SignalCatalogue from "./SignalCatalogue.svelte";
  import { SignalPicks } from "./signal-picks.svelte.js";
  import { markLabel } from "./vocabulary-words.svelte.js";

  interface Props {
    report: Report;
    block: GivenBlock;
    runner: PreviewRunner;
  }

  let { report, block, runner }: Props = $props();

  const id = $props.id();
  const kind = $derived(givenKind(block.panel));
  const layers = $derived(kind === "chart" ? report.givenLayers(block) : []);
  const removeReason = $derived(report.removeGivenLayerReason(block));
  let refused = $state<string | null>(null);
  let titleOpen = $state(false);

  const source = $derived(givenSource(report, block));
  const picks = new SignalPicks();
  let catalogueOpen = $state(false);
  const periods = $derived(kind === "comparison" ? report.givenComparison(block) : null);
</script>

<div class="grid gap-x-6 gap-y-5 @4xl:grid-cols-[minmax(0,1fr)_20rem]" data-testid="given-workspace">
  <div class="min-w-0 space-y-3">
    {#if kind === "chart"}
      <p class="text-sm text-foreground" data-testid="chart-caption">{report.caption(block)}</p>
    {/if}
    <div data-testid="chart-preview">
      <BlockPreview spec={report.previewSpec(block)} {runner} {kind} incomplete={report.incomplete(block)} announce />
    </div>
  </div>

  <aside
    class="min-w-0 space-y-5 @4xl:sticky @4xl:top-[calc(var(--headroom-top,0px)+1rem)] @4xl:max-h-[calc(100dvh-var(--headroom-top,0px)-2rem)] @4xl:self-start @4xl:overflow-y-auto"
    data-testid="chart-side"
  >
    {#if kind === "chart"}
      <nav aria-labelledby="{id}-heading" class="space-y-2" data-testid="signal-rail">
        <h3 id="{id}-heading" class="px-2 text-xs font-medium text-muted-foreground">On this chart</h3>
        <ul class="space-y-0.5">
          {#each layers as layer (layer.id)}
            <li class="flex min-h-11 items-center gap-2 rounded-md pl-2 hover:bg-accent/50 @4xl:min-h-9">
              <span class="min-w-0 flex-1 py-1 text-sm leading-snug text-pretty wrap-break-word">{layer.name}</span>
              <span class="shrink-0 text-xs text-muted-foreground">{markLabel(layer.mark)}</span>
              <Button
                variant="ghost"
                size="icon-touch"
                class="shrink-0"
                aria-label={`Remove ${layer.name}`}
                disabled={removeReason !== null}
                {...describe(removeReason)}
                onclick={() => (refused = report.removeGivenLayer(block, layer))}
              >
                <X aria-hidden="true" />
              </Button>
            </li>
          {/each}
        </ul>
        <SignalCatalogue {report} {source} {picks} bind:open={catalogueOpen}>
          {#snippet trigger(props)}
            <Button {...props} variant="dashed" size="touch-sm" class="w-full justify-start" data-testid="add-signal">
              <Plus aria-hidden="true" />
              Add a signal
            </Button>
          {/snippet}
        </SignalCatalogue>
        {#if refused}
          <p class="px-2 text-xs text-muted-foreground" role="status">{refused}</p>
        {/if}
      </nav>
    {:else if kind === "comparison" && periods}
      <div class="space-y-1.5">
        <p class="text-xs text-muted-foreground" id="{id}-compare">Compare</p>
        <ToggleGroup.Root
          type="single"
          variant="choice"
          size="sm"
          spacing={1}
          class="flex-wrap"
          aria-labelledby="{id}-compare"
          value={periods.preset ?? ""}
          onValueChange={(v) => v && report.setGivenComparison(block, { preset: v })}
        >
          {#each report.comparisonPresets as p (p.value)}
            <ToggleGroup.Item value={p.value}>{p.label}</ToggleGroup.Item>
          {/each}
        </ToggleGroup.Root>
        <p class="pt-1 text-xs text-muted-foreground">Two periods of your own</p>
        <div class="flex flex-wrap items-center gap-2">
          <DateSpanField
            value={periods.a}
            label="Period A"
            placeholder="Pick period A"
            onchange={(span) => report.setGivenComparison(block, { a: span, b: periods.b })}
          />
          <DateSpanField
            value={periods.b}
            label="Period B"
            placeholder="Pick period B"
            onchange={(span) => report.setGivenComparison(block, { a: periods.a, b: span })}
          />
        </div>
      </div>
    {:else}
      <fieldset class="space-y-2">
        <legend class="text-xs text-muted-foreground">{kind === "figures" ? "Figures to show" : "Columns to show"}</legend>
        <ul class="space-y-1.5">
          {#each report.givenColumns(block) as column (column.field)}
            <li class="flex items-start gap-2">
              <Checkbox
                id="{id}-col-{column.field}"
                checked={column.on}
                disabled={column.reason !== null}
                aria-describedby={column.reason ? `${id}-col-why-${column.field}` : undefined}
                onCheckedChange={() => report.toggleGivenColumn(block, column.field)}
              />
              <div>
                <Label for="{id}-col-{column.field}" variant="option">{column.label}</Label>
                {#if column.reason}
                  <p id="{id}-col-why-{column.field}" class="text-xs text-muted-foreground">{column.reason}</p>
                {/if}
              </div>
            </li>
          {/each}
        </ul>
      </fieldset>
    {/if}

    <div class="space-y-1 border-t border-border pt-3">
      <Collapsible.Root bind:open={titleOpen}>
        <Collapsible.Trigger>
          {#snippet child({ props }: { props: Record<string, unknown> })}
            <Button {...props} variant="subtle" size="xs" class="group max-w-full justify-start text-left whitespace-normal" data-testid="chart-title-range">
              <ChevronRight
                class="transition-transform group-data-[state=open]:rotate-90 motion-reduce:transition-none"
                aria-hidden="true"
              />
              {kind === "comparison" ? "Title" : "Title and range"}
            </Button>
          {/snippet}
        </Collapsible.Trigger>
        <Collapsible.Content class="pt-2">
          <BlockTitleRange {report} {block} rangeId="block-range-{block.key}" />
        </Collapsible.Content>
      </Collapsible.Root>
      {#if kind === "chart"}
        <GivenChartAdvanced {report} {block} />
      {/if}
    </div>
  </aside>
</div>
