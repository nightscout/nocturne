<script lang="ts">
  import { resolve } from "$app/paths";
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import ArrowLeft from "lucide-svelte/icons/arrow-left";
  import Activity from "lucide-svelte/icons/activity";
  import Info from "lucide-svelte/icons/info";
  import { getDataQualityReport } from "$api/reports.remote";
  import { requireDateParamsContext } from "$lib/hooks/date-params.svelte";
  import { contextResource } from "$lib/hooks/resource-context.svelte";
  import { buildDayBuckets, type DayBucket } from "$lib/components/reports/sensor-integrity/buckets";
  import NoiseClusterStrips from "$lib/components/reports/sensor-integrity/NoiseClusterStrips.svelte";
  import NoiseDayChart from "$lib/components/reports/sensor-integrity/NoiseDayChart.svelte";
  import DataQualityHeadline from "$lib/components/reports/sensor-integrity/DataQualityHeadline.svelte";
  import HypoAfterNoiseLog from "$lib/components/reports/sensor-integrity/HypoAfterNoiseLog.svelte";
  import ClusterTable from "$lib/components/reports/sensor-integrity/ClusterTable.svelte";
  import HypoMarker from "$lib/components/reports/sensor-integrity/HypoMarker.svelte";
  import ChartKey from "$lib/components/charts/print/ChartKey.svelte";
  import { setReportPrintMeta } from "$lib/components/reports/print/report-print.svelte";
  import type { GlucoseCluster } from "$lib/api";

  setReportPrintMeta(() => ({ title: "Signal Integrity" }));

  const params = requireDateParamsContext(14);

  const resource = contextResource(() => getDataQualityReport(params.dateRangeInput), {
    errorTitle: "Error Loading Data Quality Report",
  });

  const integrity = $derived(resource.current?.integrity);
  const entries = $derived(resource.current?.entries ?? []);
  const clusters = $derived(integrity?.clusters ?? []);
  const hypoEvents = $derived(integrity?.hypoEvents ?? []);
  const summary = $derived(integrity?.summary);

  const buckets = $derived(buildDayBuckets(entries, clusters, hypoEvents));

  let selectedDateMs = $state<number | null>(null);
  const selectedBucket = $derived(buckets.find((b) => b.dateMs === selectedDateMs) ?? null);

  function selectDay(b: DayBucket) {
    selectedDateMs = selectedDateMs === b.dateMs ? null : b.dateMs;
  }

  function selectCluster(c: GlucoseCluster) {
    const match = buckets.find((b) => b.bands.some((band) => band.cluster === c));
    if (match) selectedDateMs = match.dateMs;
  }
</script>

<svelte:head>
  <title>Signal Integrity - Nocturne Reports</title>
</svelte:head>

{#if resource.current}
  <div class="@container container mx-auto max-w-6xl space-y-6 p-3 @md:p-6">
    <!-- Header -->
    <div class="space-y-3">
      <a
        href={resolve("/reports/data-quality")}
        class="inline-flex items-center gap-1 text-sm text-muted-foreground hover:text-foreground print:hidden"
      >
        <ArrowLeft class="h-4 w-4" />
        Data Quality
      </a>
      <div class="flex items-center gap-3">
        <div class="flex h-10 w-10 items-center justify-center rounded-lg bg-primary/10 print:hidden">
          <Activity class="h-5 w-5 text-primary" />
        </div>
        <div>
          <h1 class="text-2xl font-bold tracking-tight print:hidden">Signal Integrity</h1>
          <p class="text-muted-foreground">
            Windows where readings oscillate in a way that is unlikely to be physiologic
          </p>
        </div>
      </div>
    </div>

    <DataQualityHeadline {summary} />

    <!-- Daily overview: taller than a page at 28 days, so it may break between rows -->
    <Card class="print:break-inside-auto!">
      <CardHeader class="pb-3">
        <div class="flex flex-wrap items-center justify-between gap-2">
          <div>
            <CardTitle class="text-base">Daily overview</CardTitle>
            <CardDescription
              >Each row is one day. <span class="print:hidden">Select a day to expand it.</span
              ></CardDescription
            >
          </div>
          <div class="flex flex-wrap items-center gap-x-4 gap-y-1 text-xs text-muted-foreground">
            <ChartKey
              items={[
                { texture: "cluster-low", label: "Low confidence" },
                { texture: "cluster-medium", label: "Medium confidence" },
                { texture: "cluster-high", label: "High confidence" },
              ]}
            />
            {#each [{ nocturnal: true, label: "Overnight hypo nadir" }, { nocturnal: false, label: "Daytime hypo nadir" }] as marker (marker.label)}
              <span class="flex items-center gap-1.5">
                <svg viewBox="0 0 10 8" class="h-2.5 w-3" aria-hidden="true">
                  <HypoMarker x={5} y={7} nocturnal={marker.nocturnal} />
                </svg>
                {marker.label}
              </span>
            {/each}
          </div>
        </div>
      </CardHeader>
      <CardContent>
        <NoiseClusterStrips {buckets} {selectedDateMs} onSelectDay={selectDay} />
      </CardContent>
    </Card>

    <!-- Expanded day -->
    {#if selectedBucket}
      <Card>
        <CardHeader class="pb-3">
          <CardTitle class="text-base">{selectedBucket.label}</CardTitle>
          <CardDescription>
            {selectedBucket.clusterCount}
            {selectedBucket.clusterCount === 1 ? "flagged window" : "flagged windows"}
          </CardDescription>
        </CardHeader>
        <CardContent>
          <div class="h-64">
            <NoiseDayChart bucket={selectedBucket} detailed />
          </div>
        </CardContent>
      </Card>
    {/if}

    <!-- Hypo-after-window log -->
    <Card>
      <CardHeader class="pb-3">
        <CardTitle class="text-base">Hypoglycemia after a flagged window</CardTitle>
        <CardDescription>
          Lows recorded within 3 hours of a flagged window, and any insulin dosed during the window.
        </CardDescription>
      </CardHeader>
      <CardContent>
        <HypoAfterNoiseLog events={hypoEvents} />
      </CardContent>
    </Card>

    <!-- Cluster table -->
    <Card>
      <CardHeader class="pb-3">
        <CardTitle class="text-base">Flagged windows</CardTitle>
        <CardDescription class="print:hidden">Select a row to expand its day above.</CardDescription>
      </CardHeader>
      <CardContent>
        <div class="overflow-x-auto print:overflow-visible">
          <ClusterTable {clusters} onSelect={selectCluster} />
        </div>
      </CardContent>
    </Card>

    <!-- Caveat -->
    <div class="flex items-start gap-2 rounded-lg border bg-muted/30 p-4 text-sm text-muted-foreground">
      <Info class="mt-0.5 h-4 w-4 shrink-0" />
      <p>
        This is a retrospective analysis of recorded readings. A flagged window indicates a pattern
        that is statistically unlikely to be physiologic; it does not by itself identify the cause.
        Boundaries are determined after the fact from surrounding readings. Discuss any patterns with
        your care team.
      </p>
    </div>
  </div>
{/if}
