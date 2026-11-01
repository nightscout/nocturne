<!--
  A report result as a stack of panels: the report's title and warnings, then each panel by its kind
  (a chart of tracks of layers, figures, a table or two periods) and the summary the API wrote for it. Every word is the API's or the host's (`labels`);
  the title, panel titles, warnings and summaries are written as text. Host controls go in
  `controls`, under each panel's drawing.
-->
<script lang="ts">
  import type { Snippet } from "svelte";
  import type { ChartFormat } from "../format.js";
  import ComparisonChart from "./ComparisonChart.svelte";
  import DataTable from "./DataTable.svelte";
  import LayeredChart from "./LayeredChart.svelte";
  import StatTiles from "./StatTiles.svelte";
  import TriangleAlert from "@lucide/svelte/icons/triangle-alert";
  import type { ReportViewLabels } from "./report-view-labels.js";
  import type { ReportPanel, ReportResult, ReportRow } from "./types.js";

  interface Props {
    report: ReportResult;
    /** Locale and hour cycle; the time zone comes from the report. */
    format: Omit<ChartFormat, "timeZone">;
    labels: ReportViewLabels;
    /** Inline panels have a fixed height; fullscreen panels share the height they are given. */
    displayMode?: "inline" | "fullscreen";
    /** The level of the report's heading; panel headings are one below. */
    headingLevel?: 1 | 2 | 3;
    /** Called when the reader picks a table row; rows are not selectable without it. */
    onselectrow?: (panel: ReportPanel, row: ReportRow, index: number) => void;
    /** Which panels' rows can be picked; every table's when absent. */
    selectable?: (panel: ReportPanel) => boolean;
    /** The picked row. */
    selected?: { panel: string; row: number } | null;
    /** The host's controls for a panel, drawn under its chart, figures or table. */
    controls?: Snippet<[ReportPanel]>;
  }

  let {
    report,
    format,
    labels,
    displayMode = "inline",
    headingLevel = 1,
    onselectrow,
    selectable,
    selected = null,
    controls,
  }: Props = $props();

  /** The report's zone when this browser knows it, so times match the summaries; else the browser's own. */
  function knownZone(zone: string): string | undefined {
    try {
      new Intl.DateTimeFormat("en", { timeZone: zone });
      return zone;
    } catch {
      return undefined;
    }
  }

  const zoned = $derived<ChartFormat>({ ...format, timeZone: knownZone(report.timeZone ?? "UTC") });
  const trackHeight = $derived(displayMode === "fullscreen" ? 360 : 240);
  const tableHeight = $derived(displayMode === "fullscreen" ? "max-h-[45vh]" : "max-h-[280px]");
  const colorMode = $derived(report.glucoseColorMode ?? "continuous");
  const reportHeading = $derived(`h${headingLevel}`);

  function printsItsFigures(panel: ReportPanel): boolean {
    if (panel.kind === "stat_tiles") return true;
    const layers = panel.tracks.flatMap((t) => t.layers);
    const bars = layers[0];
    return layers.length === 1 && bars.mark === "bars" && bars.bindings.stacked === true
      && bars.bindings.orientation === "horizontal" && bars.rows.length === 1;
  }
  const panelHeading = $derived(`h${headingLevel + 1}`);
</script>

{#snippet facts(panel: ReportPanel)}
  <dl class="grid grid-cols-2 gap-x-6 gap-y-3 @md:grid-cols-3 @3xl:grid-cols-6" data-testid="report-facts">
    {#each panel.facts ?? [] as fact (fact.label)}
      <div class="min-w-0">
        <dt class="truncate text-xs text-muted-foreground">{fact.label}</dt>
        <dd class="mt-0.5 text-base leading-tight font-semibold tabular-nums">
          {fact.value}{#if fact.unit}<span class="ml-1 text-xs font-normal text-muted-foreground">{fact.unit}</span>{/if}
        </dd>
        {#if fact.note}
          <dd class="mt-0.5 text-xs text-pretty text-muted-foreground">{fact.note}</dd>
        {/if}
      </div>
    {/each}
  </dl>
{/snippet}

<article class="@container flex flex-col gap-6" data-testid="report-view">
  {#if report.spec.title || (report.warnings ?? []).length > 0}
    <header class="flex flex-col gap-1.5">
      {#if report.spec.title}
        <svelte:element this={reportHeading} class="text-lg leading-tight font-semibold tracking-tight text-balance">
          {report.spec.title}
        </svelte:element>
      {/if}
      {#each report.warnings ?? [] as warning (warning)}
        <p class="flex items-start gap-1.5 text-xs text-muted-foreground" role="status" data-testid="report-warning">
          <TriangleAlert class="mt-px size-3.5 shrink-0 text-warning" aria-hidden="true" />
          <span>{warning}</span>
        </p>
      {/each}
    </header>
  {/if}
  {#each report.panels as panel, i (panel.id)}
    {@const described = (panel.facts ?? []).length > 0 || !!panel.sentence}
    <section
      aria-labelledby="panel-{panel.id}"
      class={["flex flex-col gap-3", i > 0 && "border-t border-border pt-6"]}
      data-testid="report-panel"
    >
      <div class="flex flex-col gap-0.5">
        <svelte:element this={panelHeading} id="panel-{panel.id}" class="text-sm leading-tight font-semibold">
          {panel.title ?? panel.dataset ?? ""}
        </svelte:element>
        {#if panel.context}
          <p class="text-xs text-muted-foreground tabular-nums" data-testid="report-context">{panel.context}</p>
        {/if}
      </div>
      {#if panel.status !== "ok"}
        <!-- The sentence below says why. -->
      {:else if panel.kind === "chart"}
        <LayeredChart {panel} format={zoned} {trackHeight} lineColorMode={colorMode} labels={labels.chart} />
      {:else if panel.kind === "comparison"}
        <ComparisonChart {panel} format={zoned} emptyText={labels.noData} />
      {:else if panel.kind === "stat_tiles"}
        <StatTiles {panel} format={zoned} emptyText={labels.noData} />
      {:else if panel.kind === "table"}
        <DataTable
          {panel}
          format={zoned}
          heightClass={tableHeight}
          emptyText={labels.noData}
          onselect={onselectrow && (selectable?.(panel) ?? true) ? (row, i) => onselectrow(panel, row, i) : undefined}
          selected={selected?.panel === panel.id ? selected.row : null}
        />
      {/if}
      {@render controls?.(panel)}
      {#if described}
        <!-- Stat tiles, and one stacked bar's key, print the panel's figures already. -->
        {#if panel.status === "ok" && !printsItsFigures(panel) && (panel.facts ?? []).length > 0}
          {@render facts(panel)}
        {/if}
        {#if panel.sentence}
          <p class="max-w-[65ch] text-sm text-pretty" data-testid="report-sentence">{panel.sentence}</p>
        {/if}
        {#if panel.status === "ok" && panel.summary && panel.summary !== panel.sentence}
          <details class="text-xs text-muted-foreground" data-testid="report-summary">
            <summary
              class="w-fit cursor-pointer rounded-sm select-none hover:text-foreground focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none"
            >
              {labels.fullSummary}
            </summary>
            <p class="mt-1.5 max-w-[80ch] whitespace-pre-line">{panel.summary}</p>
          </details>
        {/if}
      {:else}
        <p class="max-w-[80ch] text-xs whitespace-pre-line text-muted-foreground">{panel.summary}</p>
      {/if}
    </section>
  {/each}
</article>
