<!--
  A given chart's advanced settings, closed at first: each signal in a ruled row (its name, how it is
  drawn, whether it has a scale of its own, its own range where its axis takes one), then a calculated
  line to add from the signals on the chart. A change that cannot be made now is disabled with its
  reason beside it.
-->
<script lang="ts">
  import { Button } from "#lib/components/ui/button/index.js";
  import { Checkbox } from "#lib/components/ui/checkbox/index.js";
  import * as Collapsible from "#lib/components/ui/collapsible/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import * as Select from "#lib/components/ui/select/index.js";
  import ChevronRight from "@lucide/svelte/icons/chevron-right";
  import Plus from "@lucide/svelte/icons/plus";
  import Trash2 from "@lucide/svelte/icons/trash-2";
  import { describe } from "./describe.js";
  import {
    CALCULATED_OPS,
    PAIRED_OPS,
    WINDOWS,
    givenSources,
    opLabel,
    windowWords,
    type CalculatedOp,
    type GivenLayer,
  } from "./given.svelte.js";
  import type { GivenBlock, Report } from "./report.svelte.js";
  import { markLabel, rangeLabel } from "./vocabulary-words.svelte.js";

  interface Props {
    report: Report;
    block: GivenBlock;
  }

  let { report, block }: Props = $props();

  const id = $props.id();
  const layers = $derived(report.givenLayers(block));
  const removeReason = $derived(report.removeGivenLayerReason(block));
  /** Why the last change could not be made, said under the list. */
  let refused = $state<string | null>(null);

  const sources = $derived(givenSources(report.catalogue, block.panel));
  let op = $state<CalculatedOp>("rolling_mean");
  let a = $state("");
  let b = $state("");
  let minutes = $state(120);
  const sourceA = $derived(sources.find((s) => s.key === a)?.source ?? sources[0]?.source ?? null);
  const sourceB = $derived(sources.find((s) => s.key === b)?.source ?? null);
  const paired = $derived(PAIRED_OPS.includes(op));
  const windowed = $derived(op === "rolling_mean" || op === "band_sd");
  const addReason = $derived(
    sources.length === 0
      ? "No signal on this chart has numbers to calculate from."
      : report.calculatedReason(block, op, sourceA, paired ? sourceB : null, windowed ? minutes : null)
  );

  function apply(reason: string | null) {
    refused = reason;
  }

  function rangeOf(layer: GivenLayer): string {
    if (layer.rangeHours === null || layer.rangeHours === undefined) return "chart";
    return String(layer.rangeHours);
  }

  function rangeWords(value: string): string {
    if (value === "chart") return "Same as the chart";
    const preset = report.vocab.rangePresets.find((p) => String(p.hours) === value);
    return preset ? rangeLabel(preset.id) : windowWords(Number(value) * 60);
  }
</script>

<Collapsible.Root>
  <Collapsible.Trigger>
    {#snippet child({ props }: { props: Record<string, unknown> })}
      <Button {...props} variant="subtle" size="xs" class="group" data-testid="block-advanced">
        <ChevronRight
          class="transition-transform group-data-[state=open]:rotate-90 motion-reduce:transition-none"
          aria-hidden="true"
        />
        Advanced
      </Button>
    {/snippet}
  </Collapsible.Trigger>
  <Collapsible.Content class="space-y-4 pt-2" data-testid="given-advanced">
    <ul class="divide-y divide-border border-y border-border">
      {#each layers as layer, i (layer.id)}
        {@const ownReason = report.ownScaleReason(block, layer, !layer.own)}
        <li class="space-y-2 py-3" data-given-layer={layer.id}>
          <p class="truncate text-sm font-medium">{layer.name}</p>
          <div class="space-y-1">
            <Label for="{id}-name-{i}" variant="option">Name in the legend</Label>
            <Input
              id="{id}-name-{i}"
              value={layer.title}
              placeholder={layer.name}
              maxlength={report.catalogue.maxTitleLength}
              autocomplete="off"
              onchange={(e) => report.renameGivenLayer(block, layer, e.currentTarget.value)}
            />
          </div>
          {#if layer.marks.length > 1}
            <div class="space-y-1">
              <Label for="{id}-mark-{i}" variant="option">Draw as</Label>
              <Select.Root
                type="single"
                value={layer.mark}
                onValueChange={(v) => v && apply(report.setGivenMark(block, layer, v))}
              >
                <Select.Trigger id="{id}-mark-{i}" size="sm" class="w-full">{markLabel(layer.mark)}</Select.Trigger>
                <Select.Content>
                  {#each layer.marks as mark (mark)}
                    <Select.Item value={mark} label={markLabel(mark)}>{markLabel(mark)}</Select.Item>
                  {/each}
                </Select.Content>
              </Select.Root>
            </div>
          {/if}
          <div class="flex items-start gap-2">
            <Checkbox
              id="{id}-own-{i}"
              checked={layer.own}
              disabled={ownReason !== null}
              aria-describedby={ownReason ? `${id}-own-why-${i}` : undefined}
              onCheckedChange={(on) => apply(report.setGivenOwnScale(block, layer, on === true))}
            />
            <div>
              <Label for="{id}-own-{i}" variant="option">Own scale</Label>
              {#if ownReason}
                <p id="{id}-own-why-{i}" class="text-xs text-muted-foreground">{ownReason}</p>
              {/if}
            </div>
          </div>
          {#if layer.rangeHours !== undefined}
            {@const choices = ["chart", ...report.vocab.rangePresets.map((p) => String(p.hours))]}
            <div class="space-y-1">
              <Label for="{id}-range-{i}" variant="option">Range</Label>
              <Select.Root
                type="single"
                value={rangeOf(layer)}
                onValueChange={(v) => v && apply(report.setGivenLayerRange(block, layer, v === "chart" ? null : Number(v)))}
              >
                <Select.Trigger id="{id}-range-{i}" size="sm" class="w-full">{rangeWords(rangeOf(layer))}</Select.Trigger>
                <Select.Content>
                  {#each choices.includes(rangeOf(layer)) ? choices : [rangeOf(layer), ...choices] as choice (choice)}
                    <Select.Item value={choice} label={rangeWords(choice)}>{rangeWords(choice)}</Select.Item>
                  {/each}
                </Select.Content>
              </Select.Root>
            </div>
          {/if}
          <Button
            variant="ghost-destructive"
            size="xs"
            disabled={removeReason !== null}
            {...describe(removeReason)}
            aria-label="Remove {layer.name}"
            onclick={() => apply(report.removeGivenLayer(block, layer))}
          >
            <Trash2 aria-hidden="true" />
            Remove
          </Button>
        </li>
      {/each}
    </ul>
    {#if refused}
      <p class="text-xs text-muted-foreground" role="status">{refused}</p>
    {/if}

    <fieldset class="space-y-2" data-testid="add-calculated">
      <legend class="text-sm font-medium">Add a calculated line</legend>
      <Select.Root type="single" value={op} onValueChange={(v) => {
        const found = CALCULATED_OPS.find((o) => o === v);
        if (found) op = found;
      }}>
        <Select.Trigger size="sm" class="w-full" aria-label="Kind of calculated line">{opLabel(op)}</Select.Trigger>
        <Select.Content>
          {#each CALCULATED_OPS as o (o)}
            <Select.Item value={o} label={opLabel(o)}>{opLabel(o)}</Select.Item>
          {/each}
        </Select.Content>
      </Select.Root>
      {#if sources.length > 0}
        <div class="space-y-1">
          <Label for="{id}-a" variant="option">{paired ? "First signal" : "From"}</Label>
          <Select.Root type="single" value={a || sources[0].key} onValueChange={(v) => (a = v)}>
            <Select.Trigger id="{id}-a" size="sm" class="w-full">{sources.find((s) => s.key === (a || sources[0].key))?.label}</Select.Trigger>
            <Select.Content>
              {#each sources as s (s.key)}
                <Select.Item value={s.key} label={s.label}>{s.label}</Select.Item>
              {/each}
            </Select.Content>
          </Select.Root>
        </div>
        {#if paired}
          <div class="space-y-1">
            <Label for="{id}-b" variant="option">Second signal</Label>
            <Select.Root type="single" value={b} onValueChange={(v) => (b = v)}>
              <Select.Trigger id="{id}-b" size="sm" class="w-full">{sources.find((s) => s.key === b)?.label ?? "Choose a signal"}</Select.Trigger>
              <Select.Content>
                {#each sources as s (s.key)}
                  <Select.Item value={s.key} label={s.label}>{s.label}</Select.Item>
                {/each}
              </Select.Content>
            </Select.Root>
          </div>
        {/if}
        {#if windowed}
          <div class="space-y-1">
            <Label for="{id}-window" variant="option">Over</Label>
            <Select.Root type="single" value={String(minutes)} onValueChange={(v) => v && (minutes = Number(v))}>
              <Select.Trigger id="{id}-window" size="sm" class="w-full">{windowWords(minutes)}</Select.Trigger>
              <Select.Content>
                {#each WINDOWS as w (w)}
                  <Select.Item value={String(w)} label={windowWords(w)}>{windowWords(w)}</Select.Item>
                {/each}
              </Select.Content>
            </Select.Root>
          </div>
        {/if}
      {/if}
      <Button
        variant="dashed"
        size="sm"
        disabled={addReason !== null}
        aria-describedby={addReason ? `${id}-add-why` : undefined}
        onclick={() => apply(report.addGivenCalculated(block, op, sourceA, paired ? sourceB : null, windowed ? minutes : null))}
      >
        <Plus aria-hidden="true" />
        Add the line
      </Button>
      {#if addReason}
        <p id="{id}-add-why" class="text-xs text-muted-foreground">{addReason}</p>
      {/if}
    </fieldset>
  </Collapsible.Content>
</Collapsible.Root>
