<!--
  Draws a tool result. Kept apart from the host connection so a test can mount it with a
  fixture result.
-->
<script lang="ts">
  import { GlucoseChart } from "@nocturne/charts";
  import { transformChartData } from "@nocturne/charts/core";
  import { chartLabels } from "./labels.js";
  import { readGlucoseReport } from "./payload.js";

  interface Props {
    /** The tool result; null until the host sends it. */
    result: { structuredContent?: unknown } | null;
    /** BCP 47 regional format for times. */
    locale: string;
  }

  let { result, locale }: Props = $props();

  const report = $derived(result ? readGlucoseReport(result.structuredContent) : null);
  const data = $derived(report ? transformChartData(report.data) : null);
  const format = $derived({
    locale,
    hour12: new Intl.DateTimeFormat(locale, { hour: "numeric" }).resolvedOptions().hour12 ?? false,
  });
</script>

<main class="bg-background p-2 text-foreground">
  {#if !result}
    <p class="p-4 text-sm text-muted-foreground">Waiting for the glucose data.</p>
  {:else if !report || !data}
    <p class="p-4 text-sm text-muted-foreground" role="alert">
      The tool result has no chart data. The view cannot show a chart.
    </p>
  {:else}
    <GlucoseChart
      {data}
      xDomain={{ from: new Date(report.window.from), to: new Date(report.window.to) }}
      now={report.window.to}
      glucoseUnits={report.units}
      labels={chartLabels}
      {format}
      heightClass="h-[420px] w-full"
    />
  {/if}
</main>
