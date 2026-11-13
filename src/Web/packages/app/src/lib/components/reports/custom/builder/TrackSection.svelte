<!--
  One track of a chart panel: its title, its height, and its layers, each a row, with a button to add
  a layer while the chart has room for one.
-->
<script lang="ts">
  import * as Select from "#lib/components/ui/select/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import ArrowDown from "@lucide/svelte/icons/arrow-down";
  import ArrowUp from "@lucide/svelte/icons/arrow-up";
  import Plus from "@lucide/svelte/icons/plus";
  import Trash2 from "@lucide/svelte/icons/trash-2";
  import LayerRow, { type LayerPreview } from "./LayerRow.svelte";
  import type { TrackMessages } from "./spec-errors.js";
  import { TRACK_HEIGHTS, type SpecDraft } from "./spec-draft.svelte.js";

  interface Props {
    draft: SpecDraft;
    index: number;
    track: number;
    messages: TrackMessages | undefined;
    /** The last preview's word on each layer of the track, by layer id. */
    previews: Map<string, LayerPreview>;
    /** The axis the chart lies along, as the layers or the last preview say. */
    xKind: string | null;
    maxTitleLength: number;
    disabled?: boolean;
  }

  let { draft, index, track, messages, previews, xKind, maxTitleLength, disabled = false }: Props = $props();

  const panel = $derived(draft.panels[index]);
  const item = $derived(panel?.tracks[track]);
  const prefix = $derived(`track-${item?.key ?? track}`);
  const height = $derived.by(() => {
    const value = item?.height;
    if (value === undefined) return "default";
    return value <= 0.6 ? "small" : value >= 1.5 ? "large" : "normal";
  });
  const heightWords: Record<string, string> = { default: "Default", small: "Small", normal: "Normal", large: "Large" };
  const HEIGHTS: Record<string, number | undefined> = { default: undefined, ...TRACK_HEIGHTS };
</script>

{#if panel && item}
  <section class="space-y-3 rounded-lg border border-border bg-muted/20 p-3" aria-label="Track {track + 1}" data-testid="track-section">
    <div class="flex items-center gap-2">
      <h4 class="text-sm font-medium">Track {track + 1}</h4>
      <div class="ml-auto flex gap-1">
        <Button variant="ghost" size="icon-xs" onclick={() => draft.moveTrack(index, track, -1)} disabled={disabled || track === 0} aria-label="Move track {track + 1} up">
          <ArrowUp aria-hidden="true" />
        </Button>
        <Button
          variant="ghost"
          size="icon-xs"
          onclick={() => draft.moveTrack(index, track, 1)}
          disabled={disabled || track === panel.tracks.length - 1}
          aria-label="Move track {track + 1} down"
        >
          <ArrowDown aria-hidden="true" />
        </Button>
        <Button
          variant="ghost-destructive"
          size="icon-xs"
          onclick={() => draft.removeTrack(index, track)}
          disabled={disabled || panel.tracks.length === 1}
          aria-label="Remove track {track + 1}"
        >
          <Trash2 aria-hidden="true" />
        </Button>
      </div>
    </div>
    {#each messages?.track ?? [] as message, i (i)}
      <p class="text-sm text-destructive" role="alert" data-testid="field-error">{message}</p>
    {/each}
    <div class="grid gap-3 @lg:grid-cols-2">
      <div class="space-y-1.5">
        <Label for="{prefix}-title" size="sm">Track title</Label>
        <Input
          id="{prefix}-title"
          value={item.title ?? ""}
          maxlength={maxTitleLength}
          oninput={(event) => draft.setTrackTitle(index, track, event.currentTarget.value)}
          autocomplete="off"
          {disabled}
        />
        {#each messages?.title ?? [] as message, i (i)}
          <p class="text-xs text-destructive" role="alert" data-testid="field-error">{message}</p>
        {/each}
      </div>
      <div class="space-y-1.5">
        <Label for="{prefix}-height" size="sm">Height</Label>
        <Select.Root
          type="single"
          value={height}
          onValueChange={(value: string) => draft.setTrackHeight(index, track, HEIGHTS[value])}
          {disabled}
        >
          <Select.Trigger id="{prefix}-height" class="w-40">{heightWords[height]}</Select.Trigger>
          <Select.Content>
            {#each Object.keys(heightWords) as option (option)}
              <Select.Item value={option} label={heightWords[option]}>{heightWords[option]}</Select.Item>
            {/each}
          </Select.Content>
        </Select.Root>
        {#each messages?.height ?? [] as message, i (i)}
          <p class="text-xs text-destructive" role="alert" data-testid="field-error">{message}</p>
        {/each}
      </div>
    </div>

    {#each item.layers as layer, l (layer.key)}
      <LayerRow
        {draft}
        {index}
        {track}
        layer={l}
        messages={messages?.layers.get(l)}
        preview={previews.get(layer.id) ?? null}
        {xKind}
        {maxTitleLength}
        {disabled}
      />
    {/each}

    <div class="flex flex-wrap items-center gap-3">
      <Button variant="outline" size="sm" onclick={() => draft.addLayer(index, track)} disabled={disabled || draft.atLayerLimit(panel)} data-testid="add-layer">
        <Plus aria-hidden="true" />
        Add a layer
      </Button>
      {#if draft.atLayerLimit(panel)}
        <p class="text-sm text-muted-foreground" data-testid="layer-limit">A chart can have at most {draft.catalogue.maxLayers} layers.</p>
      {/if}
    </div>
  </section>
{/if}
