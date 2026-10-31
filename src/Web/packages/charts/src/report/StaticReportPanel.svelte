<!--
  One chart panel of a report result in its primitive's static mode, for an image. Only the chart
  primitives draw (`staticPrimitive`); the host refuses the others before it renders.
-->
<script lang="ts">
  import GlucoseChart from "../GlucoseChart.svelte";
  import type { ChartFormat } from "../format.js";
  import type { ChartLabels } from "../labels.js";
  import type { StaticRender } from "../theme/static-theme.js";
  import { transformChartData } from "../transform.js";
  import BarChart from "./BarChart.svelte";
  import ComparisonChart from "./ComparisonChart.svelte";
  import RangeBandsChart from "./RangeBandsChart.svelte";
  import TimeSeriesChart from "./TimeSeriesChart.svelte";
  import type { ReportPanel, ReportResult } from "./types.js";

  interface Props {
    report: ReportResult;
    panel: ReportPanel;
    still: StaticRender;
    /** Locale and hour cycle; the time zone is the report's. */
    format: Omit<ChartFormat, "timeZone">;
    labels: ChartLabels;
    /** The words a panel with no rows prints. */
    noData: string;
  }

  let { report, panel, still, format, labels, noData }: Props = $props();

  const zoned = $derived<ChartFormat>({ ...format, timeZone: report.timeZone ?? "UTC" });
  const colorMode = $derived(report.glucoseColorMode ?? "continuous");
</script>

{#if panel.primitive === "time_series"}
  <TimeSeriesChart {panel} format={zoned} lineColorMode={colorMode} {labels} glucoseUnits={report.units} static={still} />
{:else if panel.primitive === "range_bands" || (panel.primitive === "glucose_chart" && !panel.chart)}
  <RangeBandsChart {panel} format={zoned} lineColorMode={colorMode} traces={panel.traces ?? []} static={still} />
{:else if panel.primitive === "bar"}
  <BarChart {panel} format={zoned} static={still} />
{:else if panel.primitive === "comparison"}
  <ComparisonChart {panel} format={zoned} emptyText={noData} static={still} />
{:else if panel.primitive === "glucose_chart" && panel.chart}
  <GlucoseChart
    data={transformChartData(panel.chart)}
    xDomain={{ from: new Date(panel.window.from), to: new Date(panel.window.to) }}
    now={panel.window.to}
    glucoseUnits={report.units}
    {labels}
    format={zoned}
    appearance={{ lineColorMode: colorMode }}
    static={{ ...still, title: panel.title ?? labels.glucose, context: panel.context ?? null }}
  />
{/if}
