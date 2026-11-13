<!--
  A dataset's parameters as a form, drawn from the JSON Schema the API's report catalogue gives
  them. A parameter left empty is not sent, so the dataset uses its default.
-->
<script lang="ts">
  import SchemaField from "./SchemaField.svelte";
  import type { FieldMessage, ParameterSchema } from "./schema-form.js";

  interface Props {
    schema: ParameterSchema;
    value: Record<string, unknown>;
    onchange: (value: Record<string, unknown>) => void;
    /** Prefix of every control's id; unique on the page. */
    idPrefix: string;
    messages: FieldMessage[];
    disabled?: boolean;
  }

  let { schema, value, onchange, idPrefix, messages, disabled = false }: Props = $props();

  const properties = $derived(Object.entries(schema.properties ?? {}));

  function set(key: string, child: unknown) {
    const next = { ...value };
    if (child === undefined) delete next[key];
    else next[key] = child;
    onchange(next);
  }
</script>

{#if properties.length === 0}
  <p class="text-xs text-muted-foreground">This data has no settings.</p>
{:else}
  <div class="grid gap-4 @lg:grid-cols-2">
    {#each properties as [key, child] (key)}
      <div class={child.type === "array" && child.items?.type === "object" ? "@lg:col-span-2" : undefined}>
        <SchemaField
          schema={child}
          value={value[key]}
          onchange={(v) => set(key, v)}
          path={key}
          id="{idPrefix}-{key}"
          {messages}
          required={(schema.required ?? []).includes(key)}
          {disabled}
        />
      </div>
    {/each}
  </div>
{/if}
