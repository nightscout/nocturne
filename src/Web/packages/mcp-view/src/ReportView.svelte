<!--
  Draws a report tool result: a heading, then each panel with its primitive and the summary the
  API wrote for it. Kept apart from the host connection so a test can mount it with a fixture.
  The host calls arrive as optional callbacks: a control shows only when the host granted its call.
-->
<script lang="ts">
  import { DataTable, GlucoseChart, RangeBandsChart, StatTiles, TimeSeriesChart } from "@nocturne/charts";
  import { transformChartData, type ReportPanel, type ReportRow } from "@nocturne/charts/core";
  import ExternalLink from "@lucide/svelte/icons/external-link";
  import History from "@lucide/svelte/icons/history";
  import { Button } from "@nocturne/ui/ui/button";
  import { Checkbox } from "@nocturne/ui/ui/checkbox";
  import { chartLabels } from "./labels.js";
  import { cohortsOf, earlierRange, omittedToggles, readReport, type ReportPatch, type SpecCohort } from "./payload.js";

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

  const report = $derived(result ? readReport(result.structuredContent) : null);
  const format = $derived({
    locale,
    hour12: new Intl.DateTimeFormat(locale, { hour: "numeric" }).resolvedOptions().hour12 ?? false,
    timeZone: knownZone(report?.timeZone ?? "UTC"),
  });

  /** The zone when this browser knows it, so times match the summaries; else the browser's own. */
  function knownZone(zone: string): string | undefined {
    try {
      new Intl.DateTimeFormat("en", { timeZone: zone });
      return zone;
    } catch {
      return undefined;
    }
  }
  const plotHeight = $derived(displayMode === "fullscreen" ? "h-[45vh] min-h-[280px]" : "h-[280px]");
  const tableHeight = $derived(displayMode === "fullscreen" ? "max-h-[45vh]" : "max-h-[280px]");
  const colorMode = $derived(report?.glucoseColorMode ?? "continuous");

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

{#snippet panelBody(panel: ReportPanel)}
  {#if panel.status !== "ok"}
    <!-- The summary below says why. -->
  {:else if panel.primitive === "time_series"}
    <TimeSeriesChart {panel} {format} heightClass={plotHeight} lineColorMode={colorMode} />
  {:else if panel.primitive === "range_bands"}
    <RangeBandsChart {panel} {format} heightClass={plotHeight} />
  {:else if panel.primitive === "stat_tiles"}
    <StatTiles {panel} {format} />
  {:else if panel.primitive === "table"}
    <DataTable
      {panel}
      {format}
      heightClass={tableHeight}
      onselect={selectEvent && panel.fields.some((f) => f.name === "summary") ? (row, i) => pickRow(panel, row, i) : undefined}
      selected={selected?.panel === panel.id ? selected.row : null}
    />
  {:else if panel.primitive === "glucose_chart" && panel.chart && report}
    <GlucoseChart
      data={transformChartData(panel.chart)}
      xDomain={{ from: new Date(panel.window.from), to: new Date(panel.window.to) }}
      now={panel.window.to}
      glucoseUnits={report.units}
      labels={chartLabels}
      {format}
      heightClass="{displayMode === 'fullscreen' ? 'h-[70vh]' : 'h-[420px]'} w-full"
      omittedTracks={omittedToggles(panel.omittedScopes ?? [])}
      appearance={{ lineColorMode: colorMode }}
    />
  {/if}
{/snippet}

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
          Open in Nocturne
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
    {#if report.spec.title}
      <h1 class="mb-2 text-base font-semibold">{report.spec.title}</h1>
    {/if}
    {#each report.warnings ?? [] as warning (warning)}
      <p class="mb-2 text-sm text-warning" role="status">{warning}</p>
    {/each}
    {#if notice}
      <p class="mb-2 whitespace-pre-line text-sm text-warning" role="alert">{notice}</p>
    {/if}
    <div class="flex flex-col gap-4">
      {#each report.panels as panel (panel.id)}
        <section aria-labelledby="panel-{panel.id}" data-testid="report-panel">
          <h2 id="panel-{panel.id}" class="mb-1 text-sm font-medium">{panel.title ?? panel.dataset}</h2>
          {@render panelBody(panel)}
          {@render panelControls(panel)}
          <p class="mt-1 whitespace-pre-line text-xs text-muted-foreground">{panel.summary}</p>
        </section>
      {/each}
    </div>
  {/if}
</main>
