<!--
  One source of a derived layer: the data it reads, that data's settings, and the field it uses. Only
  data along an axis the op takes, and fields that fold onto a grid, are offered (the API's catalogue).
  "Copy from layer" fills the source from a layer the chart already shows.
-->
<script lang="ts">
  import type { CustomReportOp } from "#lib/api/generated/nocturne-api-client.js";
  import * as Select from "#lib/components/ui/select/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import SchemaForm from "./SchemaForm.svelte";
  import type { FieldMessage, ParameterSchema } from "./schema-form.js";
  import { messagesUnder } from "./spec-errors.js";
  import type { DraftLayer, DraftSource, SpecDraft } from "./spec-draft.svelte.js";

  interface Props {
    draft: SpecDraft;
    op: CustomReportOp;
    source: DraftSource;
    /** "First source" or "Second source". */
    heading: string;
    idPrefix: string;
    /** The validator's messages under this source, their paths inside it. */
    messages: FieldMessage[];
    /** The panel's layers of a dataset, which a source can copy. */
    layers: DraftLayer[];
    onchange: (source: DraftSource) => void;
    oncopy: (from: DraftLayer) => void;
    disabled?: boolean;
  }

  let { draft, op, source, heading, idPrefix, messages, layers, onchange, oncopy, disabled = false }: Props = $props();

  const dataset = $derived(draft.dataset(source.dataset));
  const fieldNames = $derived(draft.sourceFields(source.dataset, op));
  const labelOf = $derived(new Map((dataset?.fields ?? []).map((f) => [f.name, f.unit ? `${f.label} (${f.unit})` : f.label])));
  const schema: ParameterSchema = $derived(dataset?.parameters ?? {});
  const datasetMessages = $derived([...messagesUnder(messages, "dataset.name"), ...messagesUnder(messages, "dataset").filter((m) => m.path === "")]);
  const fieldMessages = $derived(messagesUnder(messages, "field"));
  const other = $derived(messages.filter((m) => m.path === ""));

  function chooseDataset(name: string) {
    onchange({ dataset: name, parameters: {}, derived: null, field: draft.sourceFields(name, op)[0] ?? "" });
  }

  function layerName(layer: DraftLayer): string {
    return layer.title?.trim() || draft.dataset(layer.dataset)?.label || layer.id;
  }
</script>

<fieldset class="space-y-3 rounded-md border border-border p-3" data-testid="layer-source">
  <legend class="px-1 text-xs font-medium">{heading}</legend>
  {#each other as message, i (i)}
    <p class="text-xs text-destructive" role="alert" data-testid="field-error">{message.message}</p>
  {/each}
  {#if source.derived}
    <p class="text-xs text-muted-foreground">This source is worked out from other data: {draft.op(source.derived.op)?.label ?? source.derived.op}.</p>
  {:else}
    <div class="grid gap-3 @lg:grid-cols-2">
      <div class="space-y-1.5">
        <Label for="{idPrefix}-dataset" size="sm">Data</Label>
        <Select.Root type="single" value={source.dataset ?? ""} onValueChange={chooseDataset} {disabled}>
          <Select.Trigger id="{idPrefix}-dataset" class="w-full" aria-invalid={datasetMessages.length > 0}>
            {dataset?.label ?? "Choose"}
          </Select.Trigger>
          <Select.Content>
            {#each draft.sourceDatasets(op) as option (option.name)}
              <Select.Item value={option.name} label={option.label}>{option.label}</Select.Item>
            {/each}
          </Select.Content>
        </Select.Root>
        {#each datasetMessages as message, i (i)}
          <p class="text-xs text-destructive" role="alert" data-testid="field-error">{message.message}</p>
        {/each}
      </div>
      <div class="space-y-1.5">
        <Label for="{idPrefix}-field" size="sm">Field</Label>
        <Select.Root type="single" value={source.field} onValueChange={(field: string) => onchange({ ...source, field })} {disabled}>
          <Select.Trigger id="{idPrefix}-field" class="w-full" aria-invalid={fieldMessages.length > 0}>
            {labelOf.get(source.field) ?? "Choose"}
          </Select.Trigger>
          <Select.Content>
            {#each fieldNames as name (name)}
              <Select.Item value={name} label={labelOf.get(name)}>{labelOf.get(name)}</Select.Item>
            {/each}
          </Select.Content>
        </Select.Root>
        {#each fieldMessages as message, i (i)}
          <p class="text-xs text-destructive" role="alert" data-testid="field-error">{message.message}</p>
        {/each}
      </div>
    </div>
    {#if layers.length > 0}
      <div class="space-y-1.5">
        <Label for="{idPrefix}-copy" size="sm">Copy from layer</Label>
        <Select.Root
          type="single"
          value=""
          onValueChange={(key: string) => {
            const from = layers.find((l) => String(l.key) === key);
            if (from) oncopy(from);
          }}
          {disabled}
        >
          <Select.Trigger id="{idPrefix}-copy" class="w-56" data-testid="copy-source">Choose a layer</Select.Trigger>
          <Select.Content>
            {#each layers as layer (layer.key)}
              <Select.Item value={String(layer.key)} label={layerName(layer)}>{layerName(layer)}</Select.Item>
            {/each}
          </Select.Content>
        </Select.Root>
      </div>
    {/if}
    <SchemaForm
      {schema}
      value={source.parameters}
      onchange={(parameters) => onchange({ ...source, parameters })}
      idPrefix="{idPrefix}-parameter"
      messages={messagesUnder(messages, "dataset")}
      {disabled}
    />
  {/if}
</fieldset>
