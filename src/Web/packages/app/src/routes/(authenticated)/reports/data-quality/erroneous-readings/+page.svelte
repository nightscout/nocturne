<script lang="ts">
  import { resolve } from "$app/paths";
  import ArrowLeft from "@lucide/svelte/icons/arrow-left";
  import TriangleAlert from "@lucide/svelte/icons/triangle-alert";
  import Info from "@lucide/svelte/icons/info";
  import { getErroneousReadingsReport } from "#lib/api/reports.remote.js";
  import { requireDateParamsContext } from "#lib/hooks/date-params.svelte.js";
  import { contextResource } from "#lib/hooks/resource-context.svelte.js";
  import FigureStrip from "#lib/components/reports/FigureStrip.svelte";
  import ErrorDayCard from "#lib/components/reports/erroneous-readings/ErrorDayCard.svelte";
  import { minutes } from "#lib/components/reports/erroneous-readings/format.js";
  import { PrintMode } from "#lib/components/charts/print/print-mode.svelte.js";
  import {
    getReportPrintContext,
    setReportPrintPreparation,
  } from "#lib/components/reports/print/report-print.svelte.js";
  import { SvelteSet } from "svelte/reactivity";
  import { tick } from "svelte";

  const params = requireDateParamsContext(14);

  const resource = contextResource(() => getErroneousReadingsReport(params.dateRangeInput), {
    errorTitle: "Error loading erroneous readings",
  });

  const summary = $derived(resource.current?.summary);
  const days = $derived(resource.current?.days ?? []);

  /**
   * Each chart loads its day's chart data, so on screen one day at a time shows its chart: the
   * newest at first. Paper has no 'Show chart', so a printout draws every day, loaded before the
   * dialog opens.
   */
  let selectedKey = $state<string | null>(null);
  const expandedKey = $derived(
    days.some((d) => d.date === selectedKey) ? selectedKey : (days[0]?.date ?? null)
  );
  const printCtx = getReportPrintContext();
  const print = new PrintMode();
  const allCharts = $derived(!!printCtx?.printing || print.active);
  const chartsReady = new SvelteSet<string>();

  const PRINT_WAIT_MS = 30_000;
  setReportPrintPreparation(async () => {
    await tick();
    const deadline = Date.now() + PRINT_WAIT_MS;
    while (!days.every((d) => chartsReady.has(d.date ?? ""))) {
      if (Date.now() > deadline) throw new Error("Erroneous readings charts did not load");
      await new Promise((resolve) => setTimeout(resolve, 100));
    }
  });
</script>

{#if resource.current}
  <div class="@container space-y-6">
    <div class="space-y-3">
      <a
        href={resolve('reports/data-quality')}
        class="inline-flex items-center gap-1 text-sm text-muted-foreground hover:text-foreground print:hidden"
      >
        <ArrowLeft class="h-4 w-4" />
        Data quality
      </a>
      <div class="flex items-center gap-3">
        <div class="flex h-10 w-10 items-center justify-center rounded-lg bg-primary/10 print:hidden">
          <TriangleAlert class="h-5 w-5 text-primary" />
        </div>
        <p class="text-muted-foreground">
          Readings that the sensor marked as not reliable. Nocturne keeps these readings. It does
          not use them for time in range, averages, GMI, or alerts.
        </p>
      </div>
    </div>

    <FigureStrip
      figures={[
        { label: "Episodes", value: String(summary?.episodes ?? 0) },
        {
          label: "Invalid readings",
          value: String(summary?.invalidReadings ?? 0),
          note: summary?.isolatedReadings ? `${summary.isolatedReadings} on their own` : undefined,
        },
        { label: "Uncertain readings", value: String(summary?.uncertainReadings ?? 0) },
        { label: "Time not counted", value: minutes(summary?.excludedMinutes ?? 0) },
      ]}
    />

    {#if days.length === 0}
      <p class="rounded-lg border p-6 text-center text-sm text-muted-foreground">
        The sensor did not mark a reading as not reliable in this period.
      </p>
    {:else}
      {#each days as day (day.date)}
        <ErrorDayCard
          {day}
          expanded={allCharts || day.date === expandedKey}
          onExpand={() => (selectedKey = day.date ?? null)}
          onChartReady={(ready) => (ready ? chartsReady.add(day.date ?? "") : chartsReady.delete(day.date ?? ""))}
        />
      {/each}
    {/if}

    <div class="flex items-start gap-2 rounded-lg border bg-muted/30 p-4 text-sm text-muted-foreground">
      <Info class="mt-0.5 h-4 w-4 shrink-0" />
      <p>
        The uploader sends the state of the sensor with each reading. Only some uploaders can send
        this state. The sensor manufacturer's apps and the cloud connectors do not send it. An
        episode has at least two invalid readings close together. An uncertain reading still counts
        in your statistics.
      </p>
    </div>
  </div>
{/if}
