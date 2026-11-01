<!--
  One layer of a chart track: its data (a dataset, or a series worked out from one or two sources),
  that data's settings, the mark that draws it, its bindings under Advanced, and on a chart that is
  not over time its own range. The axis it is drawn on and whether it was drawn are the last
  preview's; every choice is the API's catalogue's and every message the API validator's. A message
  no control of the row shows is listed at its top.
-->
<script lang="ts" module>
  /** What the last preview said of a layer. */
  export interface LayerPreview {
    status: string;
    side: string;
    unit?: string | null;
    sentence?: string | null;
  }
</script>

<script lang="ts">
  import * as Select from "#lib/components/ui/select/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import ArrowDown from "@lucide/svelte/icons/arrow-down";
  import ArrowUp from "@lucide/svelte/icons/arrow-up";
  import Trash2 from "@lucide/svelte/icons/trash-2";
  import BindingsEditor from "./BindingsEditor.svelte";
  import RangeControl from "./RangeControl.svelte";
  import SchemaForm from "./SchemaForm.svelte";
  import SourceEditor from "./SourceEditor.svelte";
  import { messagesUnder, unshown, type LayerMessages } from "./spec-errors.js";
  import type { SpecDraft } from "./spec-draft.svelte.js";

  interface Props {
    draft: SpecDraft;
    index: number;
    track: number;
    layer: number;
    messages: LayerMessages | undefined;
    preview: LayerPreview | null;
    /** The axis the chart lies along, as its layers or the last preview say: it decides whether a layer may have its own range. */
    xKind: string | null;
    maxTitleLength: number;
    disabled?: boolean;
  }

  let { draft, index, track, layer, messages, preview, xKind, maxTitleLength, disabled = false }: Props = $props();

  const panel = $derived(draft.panels[index]);
  const item = $derived(draft.layer(index, track, layer));
  const layers = $derived(panel?.tracks[track]?.layers ?? []);
  const dataset = $derived(draft.dataset(item?.dataset));
  const op = $derived(draft.op(item?.derived?.op));
  const prefix = $derived(`layer-${item?.key ?? layer}`);
  const marks = $derived(item ? draft.marksOf(item) : []);
  const markLabel = (name: string) => draft.catalogue.marks.find((m) => m.name === name)?.label ?? name;
  const slots = $derived(dataset?.marks.find((m) => m.name === item?.mark)?.bindings ?? []);
  const sourceValue = $derived(item?.derived ? `op:${item.derived.op}` : `dataset:${item?.dataset ?? ""}`);
  const others = $derived((panel?.tracks ?? []).flatMap((t) => t.layers).filter((l) => l.dataset && l.key !== item?.key));
  const opMessages = $derived((messages?.derived ?? []).filter((m) => !/^[ab](\.|$)/.test(m.path)));
  const ownRange = $derived(xKind === "time_of_day" || xKind === "offset" || xKind === "day");
  /** The ops a layer along the axis the panel's other layers lie along may use, and this layer's own op. */
  const ops = $derived.by(() => {
    const along = panel ? draft.opsAlong(draft.panelXKind(panel, item)) : draft.catalogue.ops;
    return op && !along.includes(op) ? [...along, op] : along;
  });
  const stray = $derived.by(() => {
    if (!messages || !item) return [];
    const keys = (schema: { properties?: Record<string, unknown> } | undefined) => new Set(Object.keys(schema?.properties ?? {}));
    const slotKeys = new Set(item.derived ? [] : slots.map((s) => s.key));
    return [
      ...messages.layer,
      ...unshown(messages.bindings, slotKeys),
      ...(item.derived ? unshown(opMessages, keys(op?.parameters)) : unshown(messages.parameters, keys(dataset?.parameters))),
      ...(item.derived ? [] : messages.derived.map((m) => m.message)),
      ...(item.derived && !item.derived.b ? messages.derived.filter((m) => /^b(\.|$)/.test(m.path)).map((m) => m.message) : []),
    ];
  });

  function choose(value: string) {
    const [kind, name] = value.split(":");
    if (kind === "op") draft.setLayerOp(index, track, layer, name);
    else draft.setLayerDataset(index, track, layer, name);
  }

  const sideWords: Record<string, string> = { left: "Left axis", right: "Right axis", none: "No axis" };
</script>

{#if item}
  <div class="space-y-4 rounded-md border border-border p-3" data-testid="layer-row" data-layer-id={item.id}>
    <div class="flex items-center gap-2">
      <h5 class="text-sm font-medium">Layer {layer + 1}</h5>
      {#if preview}
        <span class="text-xs text-muted-foreground" data-testid="layer-side">
          {sideWords[preview.side] ?? preview.side}{preview.unit ? `, ${preview.unit}` : ""}
        </span>
      {/if}
      <div class="ml-auto flex gap-1">
        <Button variant="ghost" size="icon-xs" onclick={() => draft.moveLayer(index, track, layer, -1)} disabled={disabled || layer === 0} aria-label="Move layer {layer + 1} up">
          <ArrowUp aria-hidden="true" />
        </Button>
        <Button
          variant="ghost"
          size="icon-xs"
          onclick={() => draft.moveLayer(index, track, layer, 1)}
          disabled={disabled || layer === layers.length - 1}
          aria-label="Move layer {layer + 1} down"
        >
          <ArrowDown aria-hidden="true" />
        </Button>
        <Button
          variant="ghost-destructive"
          size="icon-xs"
          onclick={() => draft.removeLayer(index, track, layer)}
          disabled={disabled || layers.length === 1}
          aria-label="Remove layer {layer + 1}"
        >
          <Trash2 aria-hidden="true" />
        </Button>
      </div>
    </div>

    {#each stray as message, i (i)}
      <p class="text-sm text-destructive" role="alert" data-testid="field-error">{message}</p>
    {/each}
    {#if preview && preview.status !== "ok" && preview.sentence}
      <p class="text-xs text-warning" role="status" data-testid="layer-status">{preview.sentence}</p>
    {/if}

    <div class="grid gap-4 @lg:grid-cols-2">
      <div class="space-y-1.5">
        <Label for="{prefix}-source" size="sm">Data</Label>
        <Select.Root type="single" value={sourceValue} onValueChange={choose} {disabled}>
          <Select.Trigger id="{prefix}-source" class="w-full" aria-invalid={(messages?.dataset.length ?? 0) > 0}>
            {op ? op.label : (dataset?.label ?? "Choose")}
          </Select.Trigger>
          <Select.Content>
            <Select.Group>
              <Select.Label>Data</Select.Label>
              {#each draft.chartDatasets as option (option.name)}
                <Select.Item value="dataset:{option.name}" label={option.label}>
                  <div class="flex flex-col gap-0.5 py-0.5">
                    <span>{option.label}</span>
                    {#if !option.permitted}
                      <span class="text-xs text-muted-foreground">Your access does not include this data.</span>
                    {/if}
                  </div>
                </Select.Item>
              {/each}
            </Select.Group>
            <Select.Group>
              <Select.Label>Worked out from other data</Select.Label>
              {#each ops as option (option.name)}
                <Select.Item value="op:{option.name}" label={option.label}>
                  <div class="flex flex-col gap-0.5 py-0.5">
                    <span>{option.label}</span>
                    <span class="text-xs text-muted-foreground">{option.description}</span>
                  </div>
                </Select.Item>
              {/each}
            </Select.Group>
          </Select.Content>
        </Select.Root>
        {#if dataset && !dataset.permitted}
          <p class="text-xs text-warning" role="status" data-testid="scope-note">Your access does not include this data, so this layer shows nothing.</p>
        {/if}
        {#each messages?.dataset ?? [] as message, i (i)}
          <p class="text-xs text-destructive" role="alert" data-testid="field-error">{message}</p>
        {/each}
      </div>

      <div class="space-y-1.5">
        <Label for="{prefix}-mark" size="sm">Show as</Label>
        <Select.Root type="single" value={item.mark} onValueChange={(name: string) => draft.setLayerMark(index, track, layer, name)} {disabled}>
          <Select.Trigger id="{prefix}-mark" class="w-full" aria-invalid={(messages?.mark.length ?? 0) > 0}>
            {markLabel(item.mark)}
          </Select.Trigger>
          <Select.Content>
            {#each marks as mark (mark.name)}
              <Select.Item value={mark.name} label={markLabel(mark.name)}>{markLabel(mark.name)}</Select.Item>
            {/each}
          </Select.Content>
        </Select.Root>
        {#each messages?.mark ?? [] as message, i (i)}
          <p class="text-xs text-destructive" role="alert" data-testid="field-error">{message}</p>
        {/each}
      </div>

      <div class="space-y-1.5">
        <Label for="{prefix}-title" size="sm">Name in the key</Label>
        <Input
          id="{prefix}-title"
          value={item.title ?? ""}
          maxlength={maxTitleLength}
          oninput={(event) => draft.setLayerTitle(index, track, layer, event.currentTarget.value)}
          autocomplete="off"
          {disabled}
        />
        {#each messages?.title ?? [] as message, i (i)}
          <p class="text-xs text-destructive" role="alert" data-testid="field-error">{message}</p>
        {/each}
      </div>

      {#if ownRange || item.range}
        <div class="space-y-1.5">
          <Label for="{prefix}-range" size="sm">Range</Label>
          <RangeControl
            id="{prefix}-range"
            value={item.range}
            onchange={(range) => draft.setLayerRange(index, track, layer, range)}
            inheritLabel="Same as the panel"
            maxHours={dataset?.maxRangeHours ?? draft.maxRangeHours}
            invalid={(messages?.range.length ?? 0) > 0}
            {disabled}
          />
          {#each messages?.range ?? [] as message, i (i)}
            <p class="text-xs text-destructive" role="alert" data-testid="field-error">{message}</p>
          {/each}
        </div>
      {/if}
    </div>

    {#if item.derived && op}
      <p class="text-xs text-muted-foreground">{op.description} {op.unitRule}</p>
      <SchemaForm
        schema={op.parameters}
        value={item.derived.parameters}
        onchange={(parameters) => draft.setOpParameters(index, track, layer, parameters)}
        idPrefix="{prefix}-op"
        messages={opMessages}
        {disabled}
      />
      <SourceEditor
        {draft}
        {op}
        source={item.derived.a}
        heading={op.sources === 2 ? "First source" : "Source"}
        idPrefix="{prefix}-a"
        messages={messagesUnder(messages?.derived ?? [], "a")}
        layers={others}
        onchange={(source) => draft.setSource(index, track, layer, "a", source)}
        oncopy={(from) => draft.copySource(index, track, layer, "a", from)}
        {disabled}
      />
      {#if item.derived.b}
        <SourceEditor
          {draft}
          {op}
          source={item.derived.b}
          heading="Second source"
          idPrefix="{prefix}-b"
          messages={messagesUnder(messages?.derived ?? [], "b")}
          layers={others}
          onchange={(source) => draft.setSource(index, track, layer, "b", source)}
          oncopy={(from) => draft.copySource(index, track, layer, "b", from)}
          {disabled}
        />
      {/if}
    {:else if dataset}
      <SchemaForm
        schema={dataset.parameters}
        value={item.parameters}
        onchange={(parameters) => draft.setLayerParameters(index, track, layer, parameters)}
        idPrefix="{prefix}-parameter"
        messages={messages?.parameters ?? []}
        {disabled}
      />
      <BindingsEditor
        idPrefix="{prefix}-binding"
        {slots}
        bindings={item.bindings}
        fields={dataset.fields}
        messages={messages?.bindings ?? []}
        onchange={(key, value) => draft.setLayerBinding(index, track, layer, key, value)}
        onreset={() => draft.resetLayerBindings(index, track, layer)}
        {disabled}
      />
    {/if}
  </div>
{/if}
