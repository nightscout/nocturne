<!--
  One parameter of a dataset, drawn from its JSON Schema node in the API's report catalogue: the
  control follows the node's shape (`controlFor`), and the words, limits, choices and defaults are the
  node's own. A list or a group draws its items and fields with this component again.
-->
<script lang="ts">
  import SchemaField from "./SchemaField.svelte";
  import FoodPicker from "./FoodPicker.svelte";
  import FoodCategoryInput from "./FoodCategoryInput.svelte";
  import * as Select from "#lib/components/ui/select/index.js";
  import * as ToggleGroup from "#lib/components/ui/toggle-group/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import { Switch } from "#lib/components/ui/switch/index.js";
  import Plus from "@lucide/svelte/icons/plus";
  import X from "@lucide/svelte/icons/x";
  import {
    choicesOf,
    controlFor,
    messagesAt,
    minuteToTime,
    timeToMinute,
    type FieldMessage,
    type ParameterSchema,
  } from "./schema-form.js";

  interface Props {
    schema: ParameterSchema;
    value: unknown;
    onchange: (value: unknown) => void;
    /** The path inside the dataset's parameters, as the validator names it: `cohorts[0].anchor`. */
    path: string;
    /** The element id this field's control gets; unique on the page. */
    id: string;
    messages: FieldMessage[];
    required?: boolean;
    disabled?: boolean;
  }

  let { schema, value, onchange, path, id, messages, required = false, disabled = false }: Props = $props();

  const control = $derived(controlFor(schema));
  const leaf = $derived(control !== "group" && control !== "list");
  const errors = $derived(messagesAt(messages, path, leaf));
  const title = $derived(schema.title ?? path);
  const DEFAULT_CHOICE = "__default__";

  function isRecord(v: unknown): v is Record<string, unknown> {
    return typeof v === "object" && v !== null && !Array.isArray(v);
  }

  const record = $derived(isRecord(value) ? value : undefined);
  const list = $derived(Array.isArray(value) ? value : []);

  function setChild(key: string, child: unknown) {
    const next: Record<string, unknown> = { ...(record ?? {}) };
    if (child === undefined) delete next[key];
    else next[key] = child;
    onchange(next);
  }

  /** A new object of this node's fields, each at its default. */
  function fresh(node: ParameterSchema): Record<string, unknown> {
    const out: Record<string, unknown> = {};
    for (const [key, child] of Object.entries(node.properties ?? {})) {
      if (child.default !== undefined) out[key] = child.default;
      else if ((node.required ?? []).includes(key) && child.type === "object") out[key] = fresh(child);
    }
    return out;
  }

  function setItem(index: number, item: unknown) {
    onchange(list.map((v, i) => (i === index ? item : v)));
  }

  function removeItem(index: number) {
    const next = list.filter((_, i) => i !== index);
    onchange(next.length > 0 ? next : undefined);
  }

  function readNumber(text: string): number | undefined {
    if (text.trim() === "") return undefined;
    const n = Number(text);
    return Number.isFinite(n) ? n : undefined;
  }

  const choices = $derived(choicesOf(schema));
  /** A list item's title as words inside a sentence: "Event group" as "event group". */
  const itemWords = $derived.by(() => {
    const words = schema.items?.title ?? title;
    return words.charAt(0).toLowerCase() + words.slice(1);
  });
  const selected = $derived(value ?? schema.default);
  const selectedLabel = $derived(choices.find((c) => c.value === selected)?.label);
</script>

{#snippet heading(forId: string | undefined)}
  {#if forId}
    <Label for={forId} size="sm">{title}</Label>
  {:else}
    <span class="text-xs font-medium">{title}</span>
  {/if}
  {#if schema.description}
    <p class="text-xs text-muted-foreground">{schema.description}</p>
  {/if}
{/snippet}

{#snippet problems()}
  {#each errors as message, i (i)}
    <p class="text-xs text-destructive" role="alert" data-testid="field-error">{message}</p>
  {/each}
{/snippet}

<div class="space-y-1.5" data-parameter={path} data-control={control ?? "none"}>
  {#if control === "switch"}
    <div class="flex items-start gap-3">
      <Switch
        {id}
        checked={value === undefined ? schema.default === true : value === true}
        onCheckedChange={(checked: boolean) => onchange(checked)}
        {disabled}
        aria-invalid={errors.length > 0}
      />
      <div class="space-y-1">{@render heading(id)}</div>
    </div>
  {:else if control === "number"}
    {@render heading(id)}
    <Input
      {id}
      type="number"
      class="w-40"
      value={typeof value === "number" ? value : ""}
      placeholder={typeof schema.default === "number" ? String(schema.default) : undefined}
      min={schema.minimum}
      max={schema.maximum}
      step={schema.type === "integer" ? 1 : "any"}
      oninput={(event) => onchange(readNumber(event.currentTarget.value))}
      {disabled}
      aria-invalid={errors.length > 0}
    />
  {:else if control === "select"}
    {@render heading(id)}
    <Select.Root
      type="single"
      value={value === undefined ? DEFAULT_CHOICE : String(value)}
      onValueChange={(picked: string) => {
        if (picked === DEFAULT_CHOICE) onchange(undefined);
        else onchange(choices.find((c) => String(c.value) === picked)?.value);
      }}
      {disabled}
    >
      <Select.Trigger {id} class="w-full max-w-sm" aria-invalid={errors.length > 0}>
        {#if value === undefined && selectedLabel === undefined}
          {#if required}Choose{:else}Default{/if}
        {:else}
          {selectedLabel}
        {/if}
      </Select.Trigger>
      <Select.Content>
        {#if !required}
          <Select.Item value={DEFAULT_CHOICE} label="Default">Default</Select.Item>
        {/if}
        {#each choices as choice (choice.value)}
          <Select.Item value={String(choice.value)} label={choice.label}>{choice.label}</Select.Item>
        {/each}
      </Select.Content>
    </Select.Root>
  {:else if control === "time"}
    {@render heading(id)}
    {#if typeof schema.default === "number" && value === undefined}
      <p class="text-xs text-muted-foreground">Default: {minuteToTime(schema.default)}</p>
    {/if}
    <Input
      {id}
      type="time"
      class="w-32"
      value={typeof value === "number" ? minuteToTime(value) : ""}
      oninput={(event) => {
        if (event.currentTarget.value === "") onchange(undefined);
        else {
          const minutes = timeToMinute(event.currentTarget.value);
          if (minutes !== null) onchange(minutes);
        }
      }}
      {disabled}
      aria-invalid={errors.length > 0}
    />
  {:else if control === "text"}
    {@render heading(id)}
    <Input
      {id}
      class="w-full max-w-sm"
      value={typeof value === "string" ? value : ""}
      maxlength={schema.maxLength}
      oninput={(event) => onchange(event.currentTarget.value === "" ? undefined : event.currentTarget.value)}
      {disabled}
      autocomplete="off"
      aria-invalid={errors.length > 0}
    />
  {:else if control === "date"}
    {@render heading(id)}
    <Input
      {id}
      type="date"
      class="w-44"
      value={typeof value === "string" ? value : ""}
      oninput={(event) => onchange(event.currentTarget.value === "" ? undefined : event.currentTarget.value)}
      {disabled}
      aria-invalid={errors.length > 0}
    />
  {:else if control === "food-category"}
    {@render heading(id)}
    <FoodCategoryInput {id} value={typeof value === "string" ? value : undefined} {onchange} {disabled} invalid={errors.length > 0} />
  {:else if control === "toggles"}
    <span id="{id}-label" class="text-xs font-medium">{title}</span>
    {#if schema.description}
      <p class="text-xs text-muted-foreground">{schema.description}</p>
    {/if}
    <ToggleGroup.Root
      type="multiple"
      variant="outline"
      size="sm"
      class="flex-wrap"
      aria-labelledby="{id}-label"
      value={list.map(String)}
      onValueChange={(picked: string[]) => {
        const values = choices.filter((c) => picked.includes(String(c.value))).map((c) => c.value);
        onchange(values.length > 0 ? values : undefined);
      }}
      {disabled}
    >
      {#each choices as choice (choice.value)}
        <ToggleGroup.Item value={String(choice.value)}>{choice.label}</ToggleGroup.Item>
      {/each}
    </ToggleGroup.Root>
  {:else if control === "foods"}
    {@render heading(id)}
    <FoodPicker
      {id}
      value={list.filter((v): v is string => typeof v === "string")}
      max={schema.maxItems}
      onchange={(ids: string[]) => onchange(ids.length > 0 ? ids : undefined)}
      {disabled}
    />
  {:else if control === "group"}
    <fieldset class="space-y-3 rounded-md border border-border p-3">
      {#if required}
        <legend class="px-1 text-xs font-medium">{title}</legend>
      {:else}
        <legend class="sr-only">{title}</legend>
        <div class="flex items-center gap-2">
          <Switch
            id="{id}-use"
            checked={record !== undefined}
            onCheckedChange={(on: boolean) => onchange(on ? fresh(schema) : undefined)}
            {disabled}
          />
          <Label for="{id}-use" size="sm">{title}</Label>
        </div>
      {/if}
      {#if schema.description}
        <p class="text-xs text-muted-foreground">{schema.description}</p>
      {/if}
      {#if required || record !== undefined}
        <div class="flex flex-wrap gap-4">
          {#each Object.entries(schema.properties ?? {}) as [key, child] (key)}
            <SchemaField
              schema={child}
              value={record?.[key]}
              onchange={(v) => setChild(key, v)}
              path="{path}.{key}"
              id="{id}-{key}"
              {messages}
              required={(schema.required ?? []).includes(key)}
              {disabled}
            />
          {/each}
        </div>
      {/if}
    </fieldset>
  {:else if control === "list"}
    {@const item = schema.items ?? {}}
    {@const max = schema.maxItems ?? Infinity}
    <div class="space-y-3">
      <div class="space-y-1">
        <span class="text-xs font-medium">{title}</span>
        {#if schema.description}
          <p class="text-xs text-muted-foreground">{schema.description}</p>
        {/if}
      </div>
      {#each list as entry, i (i)}
        <div class="space-y-3 rounded-md border border-border p-3" data-testid="list-item">
          <div class="flex items-center justify-between gap-2">
            <span class="text-xs font-medium">{item.title ?? title} {i + 1}</span>
            <Button variant="ghost-destructive" size="icon-xs" onclick={() => removeItem(i)} aria-label="Remove {item.title ?? title} {i + 1}" {disabled}>
              <X aria-hidden="true" />
            </Button>
          </div>
          {#each messagesAt(messages, `${path}[${i}]`, false) as message, m (m)}
            <p class="text-xs text-destructive" role="alert" data-testid="field-error">{message}</p>
          {/each}
          <div class="flex flex-wrap gap-4">
            {#each Object.entries(item.properties ?? {}) as [key, child] (key)}
              <SchemaField
                schema={child}
                value={isRecord(entry) ? entry[key] : undefined}
                onchange={(v) => {
                  const next: Record<string, unknown> = { ...(isRecord(entry) ? entry : {}) };
                  if (v === undefined) delete next[key];
                  else next[key] = v;
                  setItem(i, next);
                }}
                path="{path}[{i}].{key}"
                id="{id}-{i}-{key}"
                {messages}
                required={(item.required ?? []).includes(key)}
                {disabled}
              />
            {/each}
          </div>
        </div>
      {/each}
      <Button
        variant="outline"
        size="sm"
        onclick={() => onchange([...list, fresh(item)])}
        disabled={disabled || list.length >= max}
      >
        <Plus aria-hidden="true" />
        Add {itemWords}
      </Button>
      {#if list.length >= max}
        <p class="text-xs text-muted-foreground">This list can have at most {max} items.</p>
      {/if}
    </div>
  {:else}
    {@render heading(undefined)}
    <p class="text-xs text-destructive" role="alert">This page cannot show this setting.</p>
  {/if}
  {@render problems()}
</div>
