<script lang="ts">
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
  import Moon from "lucide-svelte/icons/moon";
  import { getDataQualityReport } from "$api/reports.remote";
  import { requireDateParamsContext } from "$lib/hooks/date-params.svelte";
  import { contextResource } from "$lib/hooks/resource-context.svelte";
  import { buildDayBuckets, type DayBucket } from "$lib/components/reports/sensor-integrity/buckets";
  import NoiseClusterStrips from "$lib/components/reports/sensor-integrity/NoiseClusterStrips.svelte";
  import NoiseDayChart from "$lib/components/reports/sensor-integrity/NoiseDayChart.svelte";
  import DataQualityHeadline from "$lib/components/reports/sensor-integrity/DataQualityHeadline.svelte";
  import HypoAfterNoiseLog from "$lib/components/reports/sensor-integrity/HypoAfterNoiseLog.svelte";
  import ClusterTable from "$lib/components/reports/sensor-integrity/ClusterTable.svelte";
  import type { GlucoseCluster } from "$lib/api";

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
        href="/reports/data-quality"
        class="inline-flex items-center gap-1 text-sm text-muted-foreground hover:text-foreground print:hidden"
      >
        <ArrowLeft class="h-4 w-4" />
        Data Quality
      </a>
      <div class="flex items-center gap-3">
        <div class="flex h-10 w-10 items-center justify-center rounded-lg bg-primary/10">
          <Activity class="h-5 w-5 text-primary" />
        </div>
        <div>
          <h1 class="text-2xl font-bold tracking-tight">Signal Integrity</h1>
          <p class="text-muted-foreground">
            Windows where readings oscillate in a way that is unlikely to be physiologic
          </p>
        </div>
      </div>
    </div>

    <DataQualityHeadline {summary} />

    <!-- Daily overview -->
    <Card>
      <CardHeader class="pb-3">
        <div class="flex flex-wrap items-center justify-between gap-2">
          <div>
            <CardTitle class="text-base">Daily overview</CardTitle>
            <CardDescription
              >Each row is one day. <span class="print:hidden">Select a day to expand it.</span
              ></CardDescription
            >
          </div>
          <!-- Legend -->
          <div class="flex flex-wrap items-center gap-3 text-xs text-muted-foreground">
            <span class="flex items-center gap-1">
              <span class="inline-block h-3 w-3 rounded-sm bg-cluster-low/40"></span>Low
            </span>
            <span class="flex items-center gap-1">
              <span class="inline-block h-3 w-3 rounded-sm bg-cluster-medium/40"></span>Medium
            </span>
            <span class="flex items-center gap-1">
              <span class="inline-block h-3 w-3 rounded-sm bg-cluster-high/40"></span>High
            </span>
            <span class="flex items-center gap-1">
              <Moon class="h-3 w-3 text-cluster-high" />Hypo nadir
            </span>
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
