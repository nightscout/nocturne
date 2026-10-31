<!--
  One panel of the report builder: the dataset, its parameters, the primitive that draws it and its
  bindings, its own range, and its part of the preview. Every choice it offers comes from the API's
  report catalogue; every message under a field is the API validator's for that field.
-->
<script lang="ts">
  import type { CustomReportRunResponse } from "#lib/api/generated/nocturne-api-client.js";
  import * as Card from "#lib/components/ui/card/index.js";
  import * as Select from "#lib/components/ui/select/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import ArrowDown from "@lucide/svelte/icons/arrow-down";
  import ArrowUp from "@lucide/svelte/icons/arrow-up";
  import LoaderCircle from "@lucide/svelte/icons/loader-circle";
  import Trash2 from "@lucide/svelte/icons/trash-2";
  import CustomReportRun from "../CustomReportRun.svelte";
  import BindingsEditor from "./BindingsEditor.svelte";
  import RangeControl from "./RangeControl.svelte";
  import SchemaForm from "./SchemaForm.svelte";
  import type { ParameterSchema } from "./schema-form.js";
  import type { PanelMessages } from "./spec-errors.js";
  import type { SpecDraft } from "./spec-draft.svelte.js";

  interface Props {
    draft: SpecDraft;
    index: number;
    messages: PanelMessages | undefined;
    /** The preview run of the whole spec as it is now, or null; this card draws its own panel of it. */
    run: CustomReportRunResponse | null;
    running: boolean;
    maxTitleLength: number;
    disabled?: boolean;
  }

  let { draft, index, messages, run, running, maxTitleLength, disabled = false }: Props = $props();

  const panel = $derived(draft.panels[index]);
  const dataset = $derived(panel ? draft.dataset(panel.dataset) : undefined);
  const primitive = $derived(panel ? draft.primitive(panel) : undefined);
  const prefix = $derived(`panel-${panel?.key ?? index}`);
  const parametersSchema: ParameterSchema = $derived(dataset?.parameters ?? {});
  const parameterKeys = $derived(new Set(Object.keys(parametersSchema.properties ?? {})));
  const slotKeys = $derived(new Set((primitive?.bindings ?? []).map((s) => s.key)));

  /** Messages whose field this card does not draw: the API's words, shown under the card's header. */
  const stray = $derived([
    ...(messages?.panel ?? []),
    ...(messages?.parameters ?? []).filter((m) => !parameterKeys.has(m.path.split(/[.[]/)[0])).map((m) => m.message),
    ...(messages?.bindings ?? []).filter((m) => !slotKeys.has(m.path.split(/[.[]/)[0])).map((m) => m.message),
  ]);

  /** This panel's part of the run, found by its id. */
  const ownResult = $derived.by(() => {
    const result: { panels?: { id?: string; omitted?: { field?: string }[] }[]; spec?: Record<string, unknown> } | undefined =
      run?.result;
    const own = result?.panels?.find((p) => p.id === panel?.id);
    if (!run || (run.errors ?? []).length > 0 || !own) return null;
    return {
      ...run,
      result: { ...result, spec: { ...result?.spec, title: null }, warnings: [], panels: [own] },
    } satisfies CustomReportRunResponse;
  });

  const omitted = $derived.by(() => {
    const own = ownResult?.result.panels[0];
    const labels = new Map((dataset?.fields ?? []).map((f) => [f.name, f.label]));
    return (own?.omitted ?? []).map((o) => (o.field ? (labels.get(o.field) ?? o.field) : "")).filter((l) => l !== "");
  });
</script>

{#if panel}
  <Card.Root data-testid="panel-card" data-panel-index={index}>
    <Card.Header>
      <Card.Title class="text-base">Panel {index + 1}</Card.Title>
      <Card.Description>{dataset?.label ?? panel.dataset}</Card.Description>
      <Card.Action>
        <div class="flex gap-1">
          <Button variant="ghost" size="icon-xs" onclick={() => draft.movePanel(index, -1)} disabled={disabled || index === 0} aria-label="Move panel {index + 1} up">
            <ArrowUp aria-hidden="true" />
          </Button>
          <Button
            variant="ghost"
            size="icon-xs"
            onclick={() => draft.movePanel(index, 1)}
            disabled={disabled || index === draft.panels.length - 1}
            aria-label="Move panel {index + 1} down"
          >
            <ArrowDown aria-hidden="true" />
          </Button>
          <Button variant="ghost-destructive" size="icon-xs" onclick={() => draft.removePanel(index)} {disabled} aria-label="Remove panel {index + 1}">
            <Trash2 aria-hidden="true" />
          </Button>
        </div>
      </Card.Action>
    </Card.Header>
    <Card.Content class="space-y-5">
      {#each stray as message, i (i)}
        <p class="text-sm text-destructive" role="alert" data-testid="field-error">{message}</p>
      {/each}

      <div class="grid gap-4 @lg:grid-cols-2">
        <div class="space-y-1.5">
          <Label for="{prefix}-dataset" size="sm">Data</Label>
          <Select.Root type="single" value={panel.dataset} onValueChange={(name: string) => draft.setDataset(index, name)} {disabled}>
            <Select.Trigger id="{prefix}-dataset" class="w-full" aria-invalid={(messages?.dataset.length ?? 0) > 0}>
              {dataset?.label ?? panel.dataset}
            </Select.Trigger>
            <Select.Content>
              {#each draft.datasets as option (option.name)}
                <Select.Item value={option.name ?? ""} label={option.label}>
                  <div class="flex flex-col gap-0.5 py-0.5">
                    <span>{option.label}</span>
                    <span class="text-xs text-muted-foreground">{option.description}</span>
                    {#if !option.permitted}
                      <span class="text-xs text-muted-foreground">Your access does not include this data.</span>
                    {/if}
                  </div>
                </Select.Item>
              {/each}
            </Select.Content>
          </Select.Root>
          {#if dataset?.description}
            <p class="text-xs text-muted-foreground">{dataset.description}</p>
          {/if}
          {#if dataset && !dataset.permitted}
            <p class="text-xs text-warning" role="status" data-testid="scope-note">
              Your access does not include this data, so this panel shows nothing.
            </p>
          {/if}
          {#each messages?.dataset ?? [] as message, i (i)}
            <p class="text-xs text-destructive" role="alert" data-testid="field-error">{message}</p>
          {/each}
        </div>

        <div class="space-y-1.5">
          <Label for="{prefix}-primitive" size="sm">Show as</Label>
          <Select.Root type="single" value={panel.primitive} onValueChange={(name: string) => draft.setPrimitive(index, name)} {disabled}>
            <Select.Trigger id="{prefix}-primitive" class="w-full" aria-invalid={(messages?.primitive.length ?? 0) > 0}>
              {primitive?.label ?? panel.primitive}
            </Select.Trigger>
            <Select.Content>
              {#each dataset?.primitives ?? [] as option (option.name)}
                <Select.Item value={option.name ?? ""} label={option.label}>{option.label}</Select.Item>
              {/each}
            </Select.Content>
          </Select.Root>
          {#each messages?.primitive ?? [] as message, i (i)}
            <p class="text-xs text-destructive" role="alert" data-testid="field-error">{message}</p>
          {/each}
        </div>

        <div class="space-y-1.5">
          <Label for="{prefix}-title" size="sm">Panel title</Label>
          <Input
            id="{prefix}-title"
            value={panel.title ?? ""}
            maxlength={maxTitleLength}
            placeholder={dataset?.label}
            oninput={(event) => draft.setPanelTitle(index, event.currentTarget.value)}
            autocomplete="off"
            {disabled}
          />
          {#each messages?.title ?? [] as message, i (i)}
            <p class="text-xs text-destructive" role="alert" data-testid="field-error">{message}</p>
          {/each}
        </div>

        <div class="space-y-1.5">
          <Label for="{prefix}-range" size="sm">Range</Label>
          <RangeControl
            id="{prefix}-range"
            value={panel.range}
            onchange={(range) => draft.setPanelRange(index, range)}
            inheritLabel="Same as the report"
            maxHours={primitive?.maxRangeHours ?? draft.maxRangeHours}
            invalid={(messages?.range.length ?? 0) > 0}
            {disabled}
          />
          {#if primitive?.rangeLimit}
            <p class="text-xs text-muted-foreground" data-testid="range-limit">{primitive.rangeLimit}</p>
          {/if}
          {#each messages?.range ?? [] as message, i (i)}
            <p class="text-xs text-destructive" role="alert" data-testid="field-error">{message}</p>
          {/each}
        </div>
      </div>

      <section class="space-y-2" aria-label="Settings of panel {index + 1}">
        <h3 class="text-sm font-medium">Settings</h3>
        <SchemaForm
          schema={parametersSchema}
          value={panel.parameters}
          onchange={(parameters) => draft.setParameters(index, parameters)}
          idPrefix="{prefix}-parameter"
          messages={messages?.parameters ?? []}
          {disabled}
        />
      </section>

      <BindingsEditor
        idPrefix="{prefix}-binding"
        slots={primitive?.bindings ?? []}
        bindings={panel.bindings}
        fields={dataset?.fields ?? []}
        messages={messages?.bindings ?? []}
        onchange={(key, value) => draft.setBinding(index, key, value)}
        onreset={() => draft.resetBindings(index)}
        {disabled}
      />

      <section class="space-y-2 border-t border-border pt-4" aria-label="Preview of panel {index + 1}" data-testid="panel-preview">
        <div class="flex items-center gap-2">
          <h3 class="text-sm font-medium">Preview</h3>
          {#if running}
            <LoaderCircle class="size-3.5 animate-spin text-muted-foreground" aria-hidden="true" />
            <span class="sr-only">Nocturne is making the preview.</span>
          {/if}
        </div>
        {#if omitted.length > 0}
          <p class="text-xs text-warning" role="status" data-testid="scope-note">
            Not shown, because your access does not include it: {omitted.join(", ")}.
          </p>
        {/if}
        {#if ownResult}
          <CustomReportRun run={ownResult} />
        {:else if run && (run.errors ?? []).length > 0}
          <p class="text-sm text-muted-foreground">Correct the problems in the report to see the preview.</p>
        {:else if !running}
          <p class="text-sm text-muted-foreground">No preview.</p>
        {/if}
      </section>
    </Card.Content>
  </Card.Root>
{/if}
