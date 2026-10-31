<!--
  A report result as a stack of panels: the report's title and warnings, then each panel's
  primitive and the summary the API wrote for it. Every word is the API's or the host's (`labels`);
  the title, panel titles, warnings and summaries are written as text. Host controls go in
  `controls`, under each panel's drawing.
-->
<script lang="ts">
  import type { Snippet } from "svelte";
  import GlucoseChart from "../GlucoseChart.svelte";
  import type { ChartFormat } from "../format.js";
  import { transformChartData } from "../transform.js";
  import BarChart from "./BarChart.svelte";
  import DataTable from "./DataTable.svelte";
  import RangeBandsChart from "./RangeBandsChart.svelte";
  import StatTiles from "./StatTiles.svelte";
  import TimeSeriesChart from "./TimeSeriesChart.svelte";
  import { omittedToggles } from "./read.js";
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
    /** The host's controls for a panel, drawn under its primitive. */
    controls?: Snippet<[ReportPanel]>;
    /** Called with the API's sentence for what the reader picked in a chart; charts pick nothing without it. */
    oncontext?: (panel: ReportPanel, text: string) => void;
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
    oncontext,
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
  const plotHeight = $derived(displayMode === "fullscreen" ? "h-[45vh] min-h-[280px]" : "h-[280px]");
  const tableHeight = $derived(displayMode === "fullscreen" ? "max-h-[45vh]" : "max-h-[280px]");
  const colorMode = $derived(report.glucoseColorMode ?? "continuous");
  const reportHeading = $derived(`h${headingLevel}`);
  const panelHeading = $derived(`h${headingLevel + 1}`);
</script>

<div class="flex flex-col gap-4" data-testid="report-view">
  {#if report.spec.title || (report.warnings ?? []).length > 0}
    <div class="flex flex-col gap-2">
      {#if report.spec.title}
        <svelte:element this={reportHeading} class="text-base font-semibold">{report.spec.title}</svelte:element>
      {/if}
      {#each report.warnings ?? [] as warning (warning)}
        <p class="text-sm text-warning" role="status">{warning}</p>
      {/each}
    </div>
  {/if}
  {#each report.panels as panel (panel.id)}
    <section aria-labelledby="panel-{panel.id}" data-testid="report-panel">
      <svelte:element this={panelHeading} id="panel-{panel.id}" class="mb-1 text-sm font-medium">
        {panel.title ?? panel.dataset}
      </svelte:element>
      {#if panel.status !== "ok"}
        <!-- The summary below says why. -->
      {:else if panel.primitive === "time_series"}
        <TimeSeriesChart
          {panel}
          format={zoned}
          heightClass={plotHeight}
          lineColorMode={colorMode}
          labels={labels.chart}
          glucoseUnits={report.units}
        />
      {:else if panel.primitive === "bar"}
        <BarChart {panel} format={zoned} heightClass={plotHeight} />
      {:else if panel.primitive === "range_bands"}
        <RangeBandsChart
          {panel}
          format={zoned}
          heightClass={plotHeight}
          lineColorMode={colorMode}
          oncontext={oncontext ? (text) => oncontext(panel, text) : undefined}
        />
      {:else if panel.primitive === "stat_tiles"}
        <StatTiles {panel} format={zoned} emptyText={labels.noData} />
      {:else if panel.primitive === "table"}
        <DataTable
          {panel}
          format={zoned}
          heightClass={tableHeight}
          emptyText={labels.noData}
          onselect={onselectrow && (selectable?.(panel) ?? true) ? (row, i) => onselectrow(panel, row, i) : undefined}
          selected={selected?.panel === panel.id ? selected.row : null}
        />
      {:else if panel.primitive === "glucose_chart" && !panel.chart}
        <RangeBandsChart
          {panel}
          format={zoned}
          heightClass={plotHeight}
          lineColorMode={colorMode}
          traces={panel.traces ?? []}
          oncontext={oncontext ? (text) => oncontext(panel, text) : undefined}
        />
      {:else if panel.primitive === "glucose_chart" && panel.chart}
        <GlucoseChart
          data={transformChartData(panel.chart)}
          xDomain={{ from: new Date(panel.window.from), to: new Date(panel.window.to) }}
          now={panel.window.to}
          glucoseUnits={report.units}
          labels={labels.chart}
          format={zoned}
          heightClass="{displayMode === 'fullscreen' ? 'h-[70vh]' : 'h-[420px]'} w-full"
          omittedTracks={omittedToggles(panel.omittedScopes ?? [])}
          appearance={{ lineColorMode: colorMode }}
        />
      {/if}
      {@render controls?.(panel)}
      <p class="mt-1 whitespace-pre-line text-xs text-muted-foreground">{panel.summary}</p>
    </section>
  {/each}
</div>
