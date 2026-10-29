<!--
  Draws a report tool result: a heading, then each panel with its primitive and the summary the
  API wrote for it. Kept apart from the host connection so a test can mount it with a fixture.
-->
<script lang="ts">
  import { DataTable, GlucoseChart, StatTiles, TimeSeriesChart } from "@nocturne/charts";
  import { transformChartData, type ReportPanel } from "@nocturne/charts/core";
  import { chartLabels } from "./labels.js";
  import { omittedToggles, readReport } from "./payload.js";

  interface Props {
    /** The tool result; null until the host sends it. */
    result: { structuredContent?: unknown } | null;
    /** BCP 47 regional format for times and numbers. */
    locale: string;
    /** Inline panels have a fixed height; fullscreen panels share the height they are given. */
    displayMode?: "inline" | "fullscreen";
  }

  let { result, locale, displayMode = "inline" }: Props = $props();

  const report = $derived(result ? readReport(result.structuredContent) : null);
  const format = $derived({
    locale,
    hour12: new Intl.DateTimeFormat(locale, { hour: "numeric" }).resolvedOptions().hour12 ?? false,
  });
  const plotHeight = $derived(displayMode === "fullscreen" ? "h-[45vh] min-h-[280px]" : "h-[280px]");
  const tableHeight = $derived(displayMode === "fullscreen" ? "max-h-[45vh]" : "max-h-[280px]");
  const colorMode = $derived(report?.glucoseColorMode ?? "continuous");
</script>

{#snippet panelBody(panel: ReportPanel)}
  {#if panel.status !== "ok"}
    <!-- The summary below says why. -->
  {:else if panel.primitive === "time_series"}
    <TimeSeriesChart {panel} {format} heightClass={plotHeight} lineColorMode={colorMode} />
  {:else if panel.primitive === "stat_tiles"}
    <StatTiles {panel} {format} />
  {:else if panel.primitive === "table"}
    <DataTable {panel} {format} heightClass={tableHeight} />
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
    <div class="flex flex-col gap-4">
      {#each report.panels as panel (panel.id)}
        <section aria-labelledby="panel-{panel.id}" data-testid="report-panel">
          <h2 id="panel-{panel.id}" class="mb-1 text-sm font-medium">{panel.title ?? panel.dataset}</h2>
          {@render panelBody(panel)}
          <p class="mt-1 whitespace-pre-line text-xs text-muted-foreground">{panel.summary}</p>
        </section>
      {/each}
    </div>
  {/if}
</main>
