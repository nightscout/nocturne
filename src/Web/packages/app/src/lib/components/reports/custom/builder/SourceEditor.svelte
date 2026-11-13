<!--
  One source of a derived layer: the data it reads, that data's settings, and the field it uses. Only
  data along an axis the op takes, and fields of the data that fold onto a grid, are offered (the API's
  catalogue). Data that comes in groups asks which group the source reads, by the API's words for the groups
  once a preview has given them. "Copy from layer" fills the
  source from a layer the chart already shows. A message no control here shows is listed at the top.
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
    /** The API's words for each group of the source's data, from the last preview; numbers until there is one. */
    groupLabels?: { number: number; label: string }[];
    disabled?: boolean;
  }

  let { draft, op, source, heading, idPrefix, messages, layers, onchange, oncopy, groupLabels = [], disabled = false }: Props = $props();

  const dataset = $derived(draft.dataset(source.dataset));
  const fieldNames = $derived(draft.sourceFields(source.dataset, op));
  const labelOf = $derived(new Map((dataset?.fields ?? []).map((f) => [f.name, f.unit ? `${f.label} (${f.unit})` : f.label])));
  const schema: ParameterSchema = $derived(dataset?.parameters ?? {});
  const datasetMessages = $derived([...messagesUnder(messages, "dataset.name"), ...messagesUnder(messages, "dataset").filter((m) => m.path === "")]);
  const fieldMessages = $derived(messagesUnder(messages, "field"));
  const seriesMessages = $derived(messagesUnder(messages, "series"));
  const groups = $derived(draft.sourceGroups(source.dataset, source.parameters));
  const groupLabel = (group: number) => groupLabels.find((g) => g.number === group)?.label ?? `Group ${group}`;
  const parameterKeys = $derived(new Set(Object.keys(schema.properties ?? {})));
  /** Every message no control here shows: all of them for a source worked out from other data, which shows none. */
  const other = $derived(
    source.derived
      ? messages
      : messages.filter((m) => {
          if (m.path === "dataset" || m.path === "dataset.name" || m.path === "field" || m.path === "series") return false;
          return !(m.path.startsWith("dataset.") && parameterKeys.has(m.path.slice("dataset.".length).split(/[.[]/)[0]));
        }),
  );

  function chooseDataset(name: string) {
    const first = draft.sourceGroups(name, {}).length > 1 ? 1 : undefined;
    onchange({ dataset: name, parameters: {}, derived: null, field: draft.sourceFields(name, op)[0] ?? "", series: first });
  }

  function setParameters(parameters: Record<string, unknown>) {
    const count = draft.sourceGroups(source.dataset, parameters).length;
    const series = count > 1 ? Math.min(source.series ?? 1, count) : undefined;
    onchange({ ...source, parameters, series });
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
      {#if groups.length > 1 || seriesMessages.length > 0}
        <div class="space-y-1.5">
          <Label for="{idPrefix}-series" size="sm">Group</Label>
          <Select.Root
            type="single"
            value={source.series === undefined ? "" : String(source.series)}
            onValueChange={(value: string) => onchange({ ...source, series: Number(value) })}
            {disabled}
          >
            <Select.Trigger id="{idPrefix}-series" class="w-full" aria-invalid={seriesMessages.length > 0} data-testid="source-series">
              {source.series === undefined ? "Choose" : groupLabel(source.series)}
            </Select.Trigger>
            <Select.Content>
              {#each groups as group (group)}
                <Select.Item value={String(group)} label={groupLabel(group)}>{groupLabel(group)}</Select.Item>
              {/each}
            </Select.Content>
          </Select.Root>
          {#each seriesMessages as message, i (i)}
            <p class="text-xs text-destructive" role="alert" data-testid="field-error">{message.message}</p>
          {/each}
        </div>
      {/if}
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
      onchange={setParameters}
      idPrefix="{idPrefix}-parameter"
      messages={messagesUnder(messages, "dataset")}
      {disabled}
    />
  {/if}
</fieldset>
