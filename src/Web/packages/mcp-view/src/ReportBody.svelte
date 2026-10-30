<!--
  A report tool result, drawn by the charts package's ReportView, with the host's controls. Kept
  apart from the host connection so a test can mount it with a fixture. The host calls arrive as
  optional callbacks: a control shows only when the host granted its call.
-->
<script lang="ts">
  import { ReportView } from "@nocturne/charts";
  import { readReport, type ReportPanel, type ReportRow, type ReportViewLabels } from "@nocturne/charts/core";
  import ExternalLink from "@lucide/svelte/icons/external-link";
  import History from "@lucide/svelte/icons/history";
  import { Button } from "@nocturne/ui/ui/button";
  import { Checkbox } from "@nocturne/ui/ui/checkbox";
  import { chartLabels } from "./labels.js";
  import { cohortsOf, earlierRange, type ReportPatch, type SpecCohort } from "./payload.js";

  interface Props {
    /** The tool result; null until the host sends it. */
    result: { structuredContent?: unknown } | null;
    /** The result the assistant's own call returned, before any refinement: its cohorts stay offered when switched off. */
    original?: { structuredContent?: unknown } | null;
    /** BCP 47 regional format for times and numbers. */
    locale: string;
    /** Inline panels have a fixed height; fullscreen panels share the height they are given. */
    displayMode?: "inline" | "fullscreen";
    /** Draws the report again with a patch to its spec (refine_report); absent when the host cannot call tools. */
    refine?: (patch: ReportPatch) => Promise<void>;
    /** Hands the assistant a sentence about what the reader picked (ui/update-model-context). */
    selectEvent?: (text: string) => void;
    /** Opens a link in the host (ui/open-link). */
    openLink?: (url: string) => void;
    /** Why the last refinement failed, as the API said it. */
    notice?: string | null;
    /** A refinement is under way. */
    busy?: boolean;
  }

  let {
    result,
    original = null,
    locale,
    displayMode = "inline",
    refine,
    selectEvent,
    openLink,
    notice = null,
    busy = false,
  }: Props = $props();

  const labels: ReportViewLabels = { chart: chartLabels, noData: "No data in this range." };

  const report = $derived(result ? readReport(result.structuredContent) : null);
  const format = $derived({
    locale,
    hour12: new Intl.DateTimeFormat(locale, { hour: "numeric" }).resolvedOptions().hour12 ?? false,
  });

  const originalSpec = $derived((original ? readReport(original.structuredContent) : report)?.spec);

  let selected = $state<{ panel: string; row: number } | null>(null);

  function pickRow(panel: ReportPanel, row: ReportRow, index: number) {
    selected = { panel: panel.id, row: index };
    const text = row.summary;
    if (typeof text === "string" && text !== "") selectEvent?.(text);
  }

  function loadEarlier(panel: ReportPanel) {
    void refine?.({ panels: [{ id: panel.id, range: earlierRange(panel.window) }] });
  }

  const key = (cohort: SpecCohort) => JSON.stringify(cohort);
  const isOn = (panel: ReportPanel, cohort: SpecCohort) =>
    cohortsOf(report?.spec, panel.id).some((c) => key(c) === key(cohort));

  function toggleCohort(panel: ReportPanel, index: number, on: boolean) {
    const next = cohortsOf(originalSpec, panel.id).filter((c, i) => (i === index ? on : isOn(panel, c)));
    if (next.length === 0) return;
    void refine?.({ panels: [{ id: panel.id, dataset: { cohorts: next } }] });
  }
</script>

{#snippet panelControls(panel: ReportPanel)}
  {@const panelCohorts = panel.dataset === "event_response" ? cohortsOf(originalSpec, panel.id) : []}
  {#if (refine && panel.status === "ok") || (openLink && panel.link)}
    <div class="mt-1 flex flex-wrap items-center gap-2" data-testid="report-panel-controls">
      {#if refine && panel.status === "ok"}
        <Button variant="outline" size="sm" disabled={busy} onclick={() => loadEarlier(panel)}>
          <History class="size-4" />
          Load earlier data
        </Button>
        {#if panelCohorts.length > 1}
          <fieldset class="flex flex-wrap items-center gap-3 text-xs" aria-label="Cohorts">
            {#each panelCohorts as cohort, i (i)}
              <span class="flex items-center gap-1">
                <Checkbox
                  id="cohort-{panel.id}-{i}"
                  checked={isOn(panel, cohort)}
                  disabled={busy}
                  onCheckedChange={(checked) => toggleCohort(panel, i, checked === true)}
                />
                <label for="cohort-{panel.id}-{i}">Cohort {i + 1}: {cohort.anchor}</label>
              </span>
            {/each}
          </fieldset>
        {/if}
      {/if}
      {#if openLink && panel.link}
        <Button variant="ghost" size="sm" onclick={() => openLink(panel.link!)}>
          <ExternalLink class="size-4" />
          Open the matching report
        </Button>
      {/if}
    </div>
  {/if}
{/snippet}

<main class="bg-background p-2 text-foreground">
  {#if !result}
    <p class="p-4 text-sm text-muted-foreground">Waiting for the report data.</p>
  {:else if !report}
    <p class="p-4 text-sm text-muted-foreground" role="alert">
      The tool result has no report data. The view cannot show a report.
    </p>
  {:else}
    {#if openLink && report.openUrl}
      <div class="mb-2 flex justify-end">
        <Button variant="outline" size="sm" onclick={() => openLink(report.openUrl!)}>
          <ExternalLink class="size-4" />
          Open in Nocturne
        </Button>
      </div>
    {/if}
    {#if notice}
      <p class="mb-2 whitespace-pre-line text-sm text-warning" role="alert">{notice}</p>
    {/if}
    <ReportView
      {report}
      {format}
      {labels}
      {displayMode}
      onselectrow={selectEvent ? pickRow : undefined}
      selectable={(panel) => panel.fields.some((f) => f.name === "summary")}
      {selected}
      controls={panelControls}
    />
  {/if}
</main>
