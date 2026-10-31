<!--
  Which fields a panel draws where: the bindings its primitive takes, each offering only the fields
  the API's catalogue lists for it. The catalogue's defaults are set when a panel or primitive is
  chosen; this changes them.
-->
<script lang="ts">
  import type {
    CustomReportBindings,
    CustomReportBindingSlot,
    CustomReportField,
  } from "#lib/api/generated/nocturne-api-client.js";
  import * as Collapsible from "#lib/components/ui/collapsible/index.js";
  import * as Select from "#lib/components/ui/select/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import { Switch } from "#lib/components/ui/switch/index.js";
  import ChevronDown from "@lucide/svelte/icons/chevron-down";
  import RotateCcw from "@lucide/svelte/icons/rotate-ccw";
  import X from "@lucide/svelte/icons/x";
  import { messagesAt, type FieldMessage } from "./schema-form.js";

  interface Props {
    idPrefix: string;
    slots: CustomReportBindingSlot[];
    bindings: CustomReportBindings;
    fields: CustomReportField[];
    messages: FieldMessage[];
    onchange: (key: keyof CustomReportBindings, value: unknown) => void;
    onreset: () => void;
    disabled?: boolean;
  }

  let { idPrefix, slots, bindings, fields, messages, onchange, onreset, disabled = false }: Props = $props();

  const DEFAULT_CHOICE = "__default__";
  const labelOf = $derived(new Map(fields.map((f) => [f.name ?? "", f.label ?? f.name ?? ""])));
  let opened = $state(false);
  const open = $derived(opened || messages.length > 0);

  const KEYS = ["x", "y", "band", "series", "columns", "stacked", "orientation"] as const;
  type Key = (typeof KEYS)[number];

  function isKey(key: string | undefined): key is Key {
    return KEYS.some((k) => k === key);
  }

  function current(key: Key): unknown {
    return bindings[key];
  }

  function list(key: Key): string[] {
    const v = current(key);
    return Array.isArray(v) ? v.filter((x): x is string => typeof x === "string") : [];
  }
</script>

{#if slots.length > 0}
  <Collapsible.Root {open} onOpenChange={(o: boolean) => (opened = o)}>
    <Collapsible.Trigger>
      {#snippet child({ props })}
        <Button {...props} variant="subtle" size="xs">
          <ChevronDown class="transition-transform {open ? 'rotate-180' : ''}" aria-hidden="true" />
          Advanced
        </Button>
      {/snippet}
    </Collapsible.Trigger>
    <Collapsible.Content>
      <div class="mt-2 space-y-4 rounded-md border border-border p-3" data-testid="bindings-editor">
        {#each slots.filter((s) => isKey(s.key)) as slot (slot.key)}
          {@const key = isKey(slot.key) ? slot.key : "x"}
          {@const id = `${idPrefix}-${key}`}
          {@const problems = messagesAt(messages, key, true)}
          <div class="space-y-1.5">
            {#if slot.input === "boolean"}
              <div class="flex items-center gap-2">
                <Switch {id} checked={current(key) === true} onCheckedChange={(on: boolean) => onchange(key, on)} {disabled} />
                <Label for={id} size="sm">{slot.label}</Label>
              </div>
            {:else if slot.input === "choice"}
              <Label for={id} size="sm">{slot.label}</Label>
              <Select.Root type="single" value={String(current(key) ?? "")} onValueChange={(v: string) => onchange(key, v)} {disabled}>
                <Select.Trigger {id} class="w-44" aria-invalid={problems.length > 0}>
                  {slot.choices.find((c) => c.value === current(key))?.label ?? "Default"}
                </Select.Trigger>
                <Select.Content>
                  {#each slot.choices as choice (choice.value)}
                    <Select.Item value={choice.value ?? ""} label={choice.label}>{choice.label}</Select.Item>
                  {/each}
                </Select.Content>
              </Select.Root>
            {:else if slot.input === "field"}
              <Label for={id} size="sm">{slot.label}</Label>
              <Select.Root
                type="single"
                value={typeof current(key) === "string" ? String(current(key)) : DEFAULT_CHOICE}
                onValueChange={(v: string) => onchange(key, v === DEFAULT_CHOICE ? undefined : v)}
                {disabled}
              >
                <Select.Trigger {id} class="w-56" aria-invalid={problems.length > 0}>
                  {typeof current(key) === "string" ? labelOf.get(String(current(key))) : "None"}
                </Select.Trigger>
                <Select.Content>
                  {#if !slot.required}
                    <Select.Item value={DEFAULT_CHOICE} label="None">None</Select.Item>
                  {/if}
                  {#each slot.fields as name (name)}
                    <Select.Item value={name} label={labelOf.get(name)}>{labelOf.get(name)}</Select.Item>
                  {/each}
                </Select.Content>
              </Select.Root>
            {:else if slot.input === "fields"}
              {@const picked = list(key)}
              {@const left = slot.fields.filter((n) => !picked.includes(n))}
              <span class="text-xs font-medium">{slot.label}</span>
              {#if picked.length > 0}
                <ol class="flex flex-wrap gap-1.5" aria-label={slot.label}>
                  {#each picked as name (name)}
                    <li class="flex items-center gap-1 rounded-md border border-border bg-muted/40 py-0.5 pr-0.5 pl-2 text-xs">
                      {labelOf.get(name)}
                      <Button
                        variant="ghost"
                        size="icon-xs"
                        onclick={() => onchange(key, picked.length > 1 ? picked.filter((n) => n !== name) : undefined)}
                        aria-label="Remove {labelOf.get(name)}"
                        {disabled}
                      >
                        <X aria-hidden="true" />
                      </Button>
                    </li>
                  {/each}
                </ol>
              {/if}
              {#if left.length > 0}
                <Select.Root type="single" value="" onValueChange={(v: string) => onchange(key, [...picked, v])} {disabled}>
                  <Select.Trigger {id} class="w-56" aria-invalid={problems.length > 0}>Add a field</Select.Trigger>
                  <Select.Content>
                    {#each left as name (name)}
                      <Select.Item value={name} label={labelOf.get(name)}>{labelOf.get(name)}</Select.Item>
                    {/each}
                  </Select.Content>
                </Select.Root>
              {/if}
            {/if}
            {#each problems as message, i (i)}
              <p class="text-xs text-destructive" role="alert" data-testid="field-error">{message}</p>
            {/each}
          </div>
        {/each}
        <Button variant="ghost-muted" size="xs" onclick={onreset} {disabled}>
          <RotateCcw aria-hidden="true" />
          Use the default fields
        </Button>
      </div>
    </Collapsible.Content>
  </Collapsible.Root>
{/if}
