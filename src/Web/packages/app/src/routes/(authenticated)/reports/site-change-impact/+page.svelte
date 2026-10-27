<script lang="ts">
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "#lib/components/ui/card/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Separator } from "#lib/components/ui/separator/index.js";
  import { formatShortDate } from "#lib/utils/formatting.js";
  import Calendar from "@lucide/svelte/icons/calendar";
  import Info from "@lucide/svelte/icons/info";
  import ArrowLeft from "@lucide/svelte/icons/arrow-left";
  import HelpCircle from "@lucide/svelte/icons/circle-question-mark";
  import Clock from "@lucide/svelte/icons/clock";
  import RefreshCw from "@lucide/svelte/icons/refresh-cw";
  import SiteChangeImpactChart from "#lib/components/reports/SiteChangeImpactChart.svelte";
  import { getSiteChangeImpact } from "#lib/api/reports.remote.js";
  import { requireDateParamsContext } from "#lib/hooks/date-params.svelte.js";
  import { contextResource } from "#lib/hooks/resource-context.svelte.js";

  // Get shared date params from context (set by reports layout)
  // Default: 30 days to capture multiple site changes for meaningful analysis
  const reportsParams = requireDateParamsContext(30);

  // Create resource with automatic layout registration; `date` carries the
  // selected range so the header matches the window that was queried.
  const siteChangeResource = contextResource(
    () => getSiteChangeImpact(reportsParams.dateRangeInput),
    { errorTitle: "Error loading site change data", dateParams: reportsParams }
  );

  const isLoading = $derived(siteChangeResource.loading);
  const analysis = $derived(siteChangeResource.current?.analysis ?? null);
  const startDate = $derived(siteChangeResource.date.from);
  const endDate = $derived(siteChangeResource.date.to);
  const dayCount = $derived(siteChangeResource.date.dayCount);

  // Format date for display
  function formatDate(date: Date): string {
    return formatShortDate(date, true);
  }
</script>

{#if siteChangeResource.current}
<div class="@container space-y-8">
  <div class="space-y-4">
    <div class="flex flex-wrap items-center justify-between gap-4 print:hidden">
      <div>
        <p class="text-muted-foreground">
          Analyze glucose patterns before and after pump site changes
        </p>
      </div>
      <div class="flex items-center gap-2">
        <Button variant="outline" href="/reports">
          <ArrowLeft class="mr-2 h-4 w-4" />
          Back to reports
        </Button>
      </div>
    </div>

    <div
      class={[
        "flex flex-wrap items-center justify-between gap-4",
        !analysis?.siteChangeCount && "print:hidden",
      ]}
    >
      <div class="flex flex-wrap items-center gap-x-2 gap-y-1 text-sm print:hidden">
        <Calendar class="h-4 w-4 text-muted-foreground" />
        <span class="font-medium">{formatDate(startDate)}</span>
        <span class="text-muted-foreground">to</span>
        <span class="font-medium">{formatDate(endDate)}</span>
        <span class="text-muted-foreground">({dayCount} days)</span>
      </div>
      {#if analysis?.siteChangeCount}
        <div class="flex flex-wrap items-center gap-x-4 gap-y-2 text-sm">
          <div class="flex items-center gap-2">
            <RefreshCw class="h-4 w-4 text-muted-foreground" />
            <span class="font-medium">{analysis.siteChangeCount}</span>
            <span class="text-muted-foreground">site changes analyzed</span>
          </div>
          {#if analysis.averageDaysBetweenChanges}
            <Separator orientation="vertical" class="h-4" />
            <div class="flex items-center gap-2">
              <Calendar class="h-4 w-4 text-muted-foreground" />
              <span class="font-medium">{analysis.averageDaysBetweenChanges}</span>
              <span class="text-muted-foreground">days between changes (avg)</span>
            </div>
          {/if}
        </div>
      {/if}
    </div>
  </div>

  <Separator class="print:hidden" />

  <Card>
    <CardHeader>
      <CardTitle class="flex items-center gap-2">
        <Clock class="h-5 w-5" />
        Glucose pattern around site changes
      </CardTitle>
      <CardDescription>
        Median glucose in the hours before and after each site change, with the 25th to 75th and 10th to 90th percentile bands
      </CardDescription>
    </CardHeader>
    <CardContent>
      {#if isLoading && !siteChangeResource.current}
        <div class="flex h-[400px] items-center justify-center">
          <div class="text-center text-muted-foreground">
            <RefreshCw class="mx-auto h-8 w-8 animate-spin opacity-50" />
            <p class="mt-2">Loading site change data...</p>
          </div>
        </div>
      {:else if analysis !== null && analysis !== undefined}
        <div class="w-full">
          <SiteChangeImpactChart {analysis} />
        </div>
      {/if}
    </CardContent>
  </Card>

  <Card variant="info">
    <CardHeader>
      <CardTitle
        variant="info"
        class="flex items-center gap-2"
      >
        <HelpCircle class="h-5 w-5" />
        Understanding this report
      </CardTitle>
    </CardHeader>
    <CardContent size="sm" class="space-y-4">
      <p>
        <strong>What this shows:</strong>
        This report takes the median of your glucose readings across all your site
        changes. The bands show the 25th to 75th and 10th to 90th percentiles.
        Together, they show how your glucose control changes as your infusion site ages.
      </p>

      <div class="grid gap-4 @lg:grid-cols-2 print:grid-cols-2">
        <div>
          <p class="font-medium">Before site change (left)</p>
          <p class="text-info/80">
            Shows glucose patterns in the hours before you changed your site.
          </p>
        </div>
        <div>
          <p class="font-medium">After site change (right)</p>
          <p class="text-info/80">
            Shows glucose patterns after the fresh site is inserted. Watch for
            improvements in control indicating better insulin absorption.
          </p>
        </div>
      </div>

      <div class="border-t border-info/20 pt-3">
        <p class="font-medium">What to look for</p>
        <p class="text-info/80">
          A consistent rise in the hours before site changes is a pattern worth
          discussing with your care team.
        </p>
      </div>
    </CardContent>
  </Card>

  {#if analysis?.hasSufficientData && analysis?.summary}
    {@const summary = analysis.summary}
    {@const tirBefore = summary.timeInRangeBeforeChange ?? 0}
    {@const tirAfter = summary.timeInRangeAfterChange ?? 0}
    <Card>
      <CardHeader>
        <CardTitle class="flex items-center gap-2">
          <Info class="h-5 w-5" />
          What the averages show
        </CardTitle>
      </CardHeader>
      <CardContent class="space-y-4">
        <div>
          <p class="font-medium">Time in range around a site change</p>
          <p class="text-sm text-muted-foreground">
            {tirBefore.toFixed(0)}% before, {tirAfter.toFixed(0)}% after.
          </p>
        </div>
        <p class="text-xs text-muted-foreground">
          These are averages across {analysis.siteChangeCount ?? 0} site changes
          in this window, not a per-change result.
        </p>
      </CardContent>
    </Card>
  {/if}
</div>
{/if}
