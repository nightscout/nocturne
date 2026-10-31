<!--
  The custom report builder: a form over a report spec. The title and range of the report, then a
  card for each panel, each with its part of a live preview. The preview runs the whole spec through
  the API a moment after the last change, one run at a time; the API's validator messages appear under
  the fields they name. The page that holds the builder gives the actions (save, save as, ...).
-->
<script lang="ts">
  import type { Snippet } from "svelte";
  import { onDestroy } from "svelte";
  import { runCustomReport } from "#lib/api/generated/customReports.generated.remote.js";
  import type { CustomReportCatalogue } from "#lib/api/generated/nocturne-api-client.js";
  import * as Card from "#lib/components/ui/card/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import Plus from "@lucide/svelte/icons/plus";
  import TriangleAlert from "@lucide/svelte/icons/triangle-alert";
  import SpecErrorList from "../SpecErrorList.svelte";
  import PanelCard from "./PanelCard.svelte";
  import RangeControl from "./RangeControl.svelte";
  import { PreviewRunner } from "./preview-runner.svelte.js";
  import { SpecDraft } from "./spec-draft.svelte.js";
  import { routeSpecErrors } from "./spec-errors.js";

  interface Props {
    catalogue: CustomReportCatalogue;
    /** The spec the builder opens with: a template's, a saved report's, or none for a blank report. */
    spec?: unknown;
    /** The page's actions, given the spec as it is now and whether its last preview was valid. */
    actions: Snippet<[{ spec: () => Record<string, unknown>; valid: boolean; empty: boolean }]>;
    disabled?: boolean;
  }

  let { catalogue, spec, actions, disabled = false }: Props = $props();

  // svelte-ignore state_referenced_locally
  const draft = new SpecDraft(catalogue, spec);
  const runner = new PreviewRunner((s) => runCustomReport({ spec: s }));
  onDestroy(() => runner.dispose());

  let first = true;
  $effect(() => {
    const current = draft.toSpec();
    if (draft.panels.length === 0) return;
    if (first) {
      first = false;
      runner.now(current);
    } else {
      runner.schedule(current);
    }
  });

  const run = $derived(draft.panels.length > 0 ? runner.run : null);
  const routed = $derived(routeSpecErrors(run?.errors));
  const valid = $derived(!!run && (run.errors ?? []).length === 0);
  const result: { warnings?: string[] } | undefined = $derived(run?.result);
  const warnings = $derived(result?.warnings ?? []);
</script>

<div class="space-y-6" data-testid="report-builder">
  <Card.Root>
    <Card.Header>
      <Card.Title class="text-base">Report</Card.Title>
      <Card.Description>The title and the range of the report. A panel can have a range of its own.</Card.Description>
    </Card.Header>
    <Card.Content class="grid gap-4 @lg:grid-cols-2">
      <div class="space-y-1.5">
        <Label for="report-title" size="sm">Title</Label>
        <Input
          id="report-title"
          bind:value={draft.title}
          maxlength={draft.maxTitleLength}
          autocomplete="off"
          {disabled}
        />
      </div>
      <div class="space-y-1.5">
        <Label for="report-range" size="sm">Range</Label>
        <RangeControl
          id="report-range"
          value={draft.range}
          onchange={(range) => (draft.range = range)}
          inheritLabel="The default of each panel"
          maxHours={draft.maxRangeHours}
          invalid={routed.report.some((e) => e.path?.startsWith("range"))}
          {disabled}
        />
      </div>
    </Card.Content>
  </Card.Root>

  {#if routed.report.length > 0}
    <SpecErrorList errors={routed.report} />
  {/if}
  {#if runner.failure}
    <p class="text-sm text-destructive" role="alert">The preview could not be made. Nocturne will try again when you make a change.</p>
  {/if}
  {#each warnings as warning, i (i)}
    <p class="flex items-start gap-2 text-sm text-muted-foreground" role="status">
      <TriangleAlert class="mt-0.5 size-4 shrink-0" aria-hidden="true" />
      {warning}
    </p>
  {/each}

  {#each draft.panels as panel, index (panel.key)}
    <PanelCard
      {draft}
      {index}
      messages={routed.panels.get(index)}
      {run}
      running={runner.running}
      maxTitleLength={draft.maxTitleLength}
      {disabled}
    />
  {:else}
    <p class="rounded-lg border border-dashed border-border p-6 text-sm text-muted-foreground">
      This report has no panels. Add a panel to start.
    </p>
  {/each}

  <div class="flex flex-wrap items-center gap-3">
    <Button variant="outline" onclick={() => draft.addPanel()} disabled={disabled || draft.atPanelLimit} data-testid="add-panel">
      <Plus aria-hidden="true" />
      Add a panel
    </Button>
    {#if draft.atPanelLimit}
      <p class="text-sm text-muted-foreground" data-testid="panel-limit">A report can have at most {draft.maxPanels} panels.</p>
    {/if}
  </div>

  <div class="sticky bottom-0 z-10 -mx-1 border-t border-border bg-background/95 px-1 py-3 backdrop-blur">
    {@render actions({ spec: () => draft.toSpec(), valid, empty: draft.panels.length === 0 })}
  </div>
</div>
