<!--
  One panel of the report builder: its type (a chart, statistics tiles, a table or a comparison), its
  title and range, then for a chart its grid and its tracks of layers, and for the other types the
  dataset, its settings and its columns; and its part of the preview. Every choice it offers comes
  from the API's report catalogue; every message under a field is the API validator's for that field.
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
  import Plus from "@lucide/svelte/icons/plus";
  import Trash2 from "@lucide/svelte/icons/trash-2";
  import CustomReportRun from "../CustomReportRun.svelte";
  import BindingsEditor from "./BindingsEditor.svelte";
  import type { LayerPreview } from "./LayerRow.svelte";
  import RangeControl from "./RangeControl.svelte";
  import SchemaForm from "./SchemaForm.svelte";
  import TrackSection from "./TrackSection.svelte";
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
  const chart = $derived(panel?.kind === "chart");
  const dataset = $derived(panel && !chart ? draft.dataset(panel.dataset) : undefined);
  const prefix = $derived(`panel-${panel?.key ?? index}`);
  const kindLabel = (name: string) => draft.catalogue.panelKinds.find((k) => k.name === name)?.label ?? name;
  const columns = $derived(dataset?.panelKinds.find((k) => k.name === panel?.kind)?.columns ?? []);
  const columnSlots = $derived([{ key: "columns", label: "Columns", input: "fields", fields: columns, counts: [], required: false, choices: [] }]);
  const parameterKeys = $derived(new Set(Object.keys(dataset?.parameters?.properties ?? {})));

  /** Messages whose field this card does not draw: the API's words, shown under the card's header. */
  const stray = $derived([
    ...(messages?.panel ?? []),
    ...(messages?.tracks ?? []),
    ...(messages?.parameters ?? []).filter((m) => !parameterKeys.has(m.path.split(/[.[]/)[0])).map((m) => m.message),
    ...(messages?.bindings ?? []).filter((m) => m.path.split(/[.[]/)[0] !== "columns").map((m) => m.message),
  ]);

  interface ResultPanel {
    id?: string;
    xKind?: string | null;
    omitted?: { field?: string }[];
    tracks?: { layers?: { id?: string; status?: string; side?: string; unit?: string | null; sentence?: string | null }[] }[];
  }

  /** This panel's part of the run, found by its id. */
  const ownResult = $derived.by(() => {
    const result: { panels?: ResultPanel[]; spec?: Record<string, unknown> } | undefined = run?.result;
    const own = result?.panels?.find((p) => p.id === panel?.id);
    if (!run || (run.errors ?? []).length > 0 || !own) return null;
    return {
      ...run,
      result: { ...result, spec: { ...result?.spec, title: null }, warnings: [], panels: [own] },
    } satisfies CustomReportRunResponse;
  });
  const ownPanel = $derived<ResultPanel | null>(ownResult?.result.panels[0] ?? null);

  /** The last preview's word on each layer, by its id. */
  const previews = $derived(
    new Map(
      (ownPanel?.tracks ?? [])
        .flatMap((t) => t.layers ?? [])
        .filter((l) => l.id)
        .map((l): [string, LayerPreview] => [l.id ?? "", { status: l.status ?? "ok", side: l.side ?? "none", unit: l.unit, sentence: l.sentence }]),
    ),
  );

  const omitted = $derived.by(() => {
    const labels = new Map((dataset?.fields ?? []).map((f) => [f.name, f.label]));
    return (ownPanel?.omitted ?? []).map((o) => (o.field ? (labels.get(o.field) ?? o.field) : "")).filter((l) => l !== "");
  });

  const GRIDS = [5, 15, 30, 60];
  const gridValue = $derived(panel?.grid === null || panel?.grid === undefined ? "native" : String(panel.grid));
  const gridOptions = $derived([
    "native",
    "0",
    ...new Set([...GRIDS, ...(panel?.grid ? [panel.grid] : [])].sort((a, b) => a - b).map(String)),
  ]);
  function gridWords(value: string): string {
    if (value === "native") return "Each dataset's own spacing";
    if (value === "0") return "Every reading";
    return `Every ${value} minutes`;
  }
</script>

{#if panel}
  <Card.Root data-testid="panel-card" data-panel-index={index}>
    <Card.Header>
      <Card.Title class="text-base">Panel {index + 1}</Card.Title>
      <Card.Description>{kindLabel(panel.kind)}</Card.Description>
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
          <Label for="{prefix}-kind" size="sm">Panel type</Label>
          <Select.Root type="single" value={panel.kind} onValueChange={(kind: string) => draft.setKind(index, kind)} {disabled}>
            <Select.Trigger id="{prefix}-kind" class="w-full" aria-invalid={(messages?.kind.length ?? 0) > 0}>
              {kindLabel(panel.kind)}
            </Select.Trigger>
            <Select.Content>
              {#each draft.catalogue.panelKinds as option (option.name)}
                <Select.Item value={option.name} label={option.label}>{option.label}</Select.Item>
              {/each}
            </Select.Content>
          </Select.Root>
          {#each messages?.kind ?? [] as message, i (i)}
            <p class="text-xs text-destructive" role="alert" data-testid="field-error">{message}</p>
          {/each}
        </div>

        <div class="space-y-1.5">
          <Label for="{prefix}-title" size="sm">Panel title</Label>
          <Input
            id="{prefix}-title"
            value={panel.title ?? ""}
            maxlength={maxTitleLength}
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
            maxHours={dataset?.maxRangeHours ?? draft.maxRangeHours}
            invalid={(messages?.range.length ?? 0) > 0}
            {disabled}
          />
          {#if dataset?.rangeLimit}
            <p class="text-xs text-muted-foreground" data-testid="range-limit">{dataset.rangeLimit}</p>
          {/if}
          {#each messages?.range ?? [] as message, i (i)}
            <p class="text-xs text-destructive" role="alert" data-testid="field-error">{message}</p>
          {/each}
        </div>

        {#if chart}
          <div class="space-y-1.5">
            <Label for="{prefix}-grid" size="sm">Grid</Label>
            <Select.Root
              type="single"
              value={gridValue}
              onValueChange={(value: string) => draft.setGrid(index, value === "native" ? null : Number(value))}
              {disabled}
            >
              <Select.Trigger id="{prefix}-grid" class="w-full" aria-invalid={(messages?.grid.length ?? 0) > 0}>{gridWords(gridValue)}</Select.Trigger>
              <Select.Content>
                {#each gridOptions as option (option)}
                  <Select.Item value={option} label={gridWords(option)}>{gridWords(option)}</Select.Item>
                {/each}
              </Select.Content>
            </Select.Root>
            {#each messages?.grid ?? [] as message, i (i)}
              <p class="text-xs text-destructive" role="alert" data-testid="field-error">{message}</p>
            {/each}
          </div>
        {:else}
          <div class="space-y-1.5">
            <Label for="{prefix}-dataset" size="sm">Data</Label>
            <Select.Root type="single" value={panel.dataset} onValueChange={(name: string) => draft.setDataset(index, name)} {disabled}>
              <Select.Trigger id="{prefix}-dataset" class="w-full" aria-invalid={(messages?.dataset.length ?? 0) > 0}>
                {dataset?.label ?? panel.dataset}
              </Select.Trigger>
              <Select.Content>
                {#each draft.datasetsFor(panel.kind) as option (option.name)}
                  <Select.Item value={option.name} label={option.label}>
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
            {#if dataset && !dataset.permitted}
              <p class="text-xs text-warning" role="status" data-testid="scope-note">Your access does not include this data, so this panel shows nothing.</p>
            {/if}
            {#each messages?.dataset ?? [] as message, i (i)}
              <p class="text-xs text-destructive" role="alert" data-testid="field-error">{message}</p>
            {/each}
          </div>
        {/if}
      </div>

      {#if chart}
        {#each panel.tracks as track, t (track.key)}
          <TrackSection
            {draft}
            {index}
            track={t}
            messages={messages?.trackMessages.get(t)}
            {previews}
            xKind={ownPanel?.xKind ?? null}
            {maxTitleLength}
            {disabled}
          />
        {/each}
        <div class="flex flex-wrap items-center gap-3">
          <Button
            variant="outline"
            size="sm"
            onclick={() => draft.addTrack(index)}
            disabled={disabled || draft.atTrackLimit(panel) || draft.atLayerLimit(panel)}
            data-testid="add-track"
          >
            <Plus aria-hidden="true" />
            Add a track
          </Button>
          {#if draft.atTrackLimit(panel)}
            <p class="text-sm text-muted-foreground" data-testid="track-limit">A chart can have at most {draft.catalogue.maxTracks} tracks.</p>
          {/if}
        </div>
      {:else if dataset}
        <section class="space-y-2" aria-label="Settings of panel {index + 1}">
          <h3 class="text-sm font-medium">Settings</h3>
          <SchemaForm
            schema={dataset.parameters}
            value={panel.parameters}
            onchange={(parameters) => draft.setParameters(index, parameters)}
            idPrefix="{prefix}-parameter"
            messages={messages?.parameters ?? []}
            {disabled}
          />
        </section>
        <BindingsEditor
          idPrefix="{prefix}-binding"
          slots={columnSlots}
          bindings={{ columns: panel.columns }}
          fields={dataset.fields}
          messages={messages?.bindings ?? []}
          onchange={(_, value) => draft.setColumns(index, Array.isArray(value) ? value.filter((v) => typeof v === "string") : undefined)}
          onreset={() => draft.setColumns(index, undefined)}
          {disabled}
        />
      {/if}

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
