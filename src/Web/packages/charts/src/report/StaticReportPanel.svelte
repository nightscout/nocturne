<!--
  One chart or comparison panel of a report result in its static mode, for an image. Only those
  kinds draw (`staticPanel`); the host refuses the others before it renders.
-->
<script lang="ts">
  import type { ChartFormat } from "../format.js";
  import type { ChartLabels } from "../labels.js";
  import type { StaticRender } from "../theme/static-theme.js";
  import ComparisonChart from "./ComparisonChart.svelte";
  import StaticLayeredChart from "./static/StaticLayeredChart.svelte";
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
    /** How many of a comparison's figures did not fit the image. */
    moreRows: (count: number) => string;
  }

  let { report, panel, still, format, labels, noData, moreRows }: Props = $props();

  /** The tenant's regional format and clock where the report carries them, else the host's. */
  const zoned = $derived<ChartFormat>({
    locale: report.locale ?? format.locale,
    hour12: report.hour12 ?? format.hour12,
    timeZone: report.timeZone ?? "UTC",
  });
  const colorMode = $derived(report.glucoseColorMode ?? "continuous");
</script>

{#if panel.kind === "chart"}
  <StaticLayeredChart {still} {panel} format={zoned} lineColorMode={colorMode} {labels} title={panel.title ?? ""} />
{:else if panel.kind === "comparison"}
  <ComparisonChart {panel} format={zoned} emptyText={noData} static={still} {moreRows} />
{/if}
