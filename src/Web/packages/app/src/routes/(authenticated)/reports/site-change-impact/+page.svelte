<script lang="ts">
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { Button } from "$lib/components/ui/button";
  import { Separator } from "$lib/components/ui/separator";
  import { formatShortDate } from "$lib/utils/formatting";
  import {
    Calendar,
    Info,
    TrendingUp,
    TrendingDown,
    ArrowLeft,
    Printer,
    HelpCircle,
    Clock,
    Lightbulb,
    RefreshCw,
    Target,
  } from "lucide-svelte";
  import SiteChangeIcon from "$lib/components/icons/SiteChangeIcon.svelte";
  import SiteChangeImpactChart from "$lib/components/reports/SiteChangeImpactChart.svelte";
  import { getSiteChangeImpact } from "$api/reports.remote";
  import { requireDateParamsContext } from "$lib/hooks/date-params.svelte";
  import { contextResource } from "$lib/hooks/resource-context.svelte";

  // Get shared date params from context (set by reports layout)
  // Default: 30 days to capture multiple site changes for meaningful analysis
  const reportsParams = requireDateParamsContext(30);

  // Create resource with automatic layout registration; `date` carries the
  // selected range so the header matches the window that was queried.
  const siteChangeResource = contextResource(
    () => getSiteChangeImpact(reportsParams.dateRangeInput),
    { errorTitle: "Error Loading Site Change Data", dateParams: reportsParams }
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

<svelte:head>
  <title>Site Change Impact - Nocturne Reports</title>
  <meta
    name="description"
    content="Analyze how pump site changes affect your glucose control"
  />
</svelte:head>

{#if siteChangeResource.current}
<div class="@container container mx-auto max-w-7xl space-y-8 p-3 @md:p-6">
  <!-- Header -->
  <div class="space-y-4">
    <div class="flex flex-wrap items-center justify-between gap-4">
      <div>
        <h1 class="flex items-center gap-3 text-2xl font-bold @md:text-3xl">
          <SiteChangeIcon class="h-6 w-6 text-rose-600 @md:h-8 @md:w-8" />
          Site Change Impact
        </h1>
        <p class="mt-1 text-muted-foreground">
          Analyze glucose patterns before and after pump site changes
        </p>
      </div>
      <div class="flex items-center gap-2 print:hidden">
        <Button
          variant="outline"
          onclick={() => window.print()}
          class="hidden md:flex"
        >
          <Printer class="mr-2 h-4 w-4" />
          Print
        </Button>
        <Button variant="outline" href="/reports">
          <ArrowLeft class="mr-2 h-4 w-4" />
          Back to Reports
        </Button>
      </div>
    </div>

    <!-- Date Range Info -->
    <Card class="bg-muted/30">
      <CardContent
        class="flex flex-wrap items-center justify-between gap-4 py-3"
      >
        <div class="flex items-center gap-2 text-sm">
          <Calendar class="h-4 w-4 text-muted-foreground" />
          <span class="font-medium">{formatDate(startDate)}</span>
          <span class="text-muted-foreground">to</span>
          <span class="font-medium">{formatDate(endDate)}</span>
          <span class="text-muted-foreground">({dayCount} days)</span>
        </div>
        {#if analysis?.siteChangeCount}
          <div class="flex items-center gap-4 text-sm">
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
      </CardContent>
    </Card>
  </div>

  <Separator />

  <!-- Main Chart -->
  <Card>
    <CardHeader>
      <CardTitle class="flex items-center gap-2">
        <Clock class="h-5 w-5" />
        Glucose Pattern Around Site Changes
      </CardTitle>
      <CardDescription>
        Average glucose levels in the hours before and after each site change
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

  <!-- Educational Card -->
  <Card
    class="border-blue-200 bg-blue-50/50 dark:border-blue-900 dark:bg-blue-950/20"
  >
    <CardHeader>
      <CardTitle
        class="flex items-center gap-2 text-blue-700 dark:text-blue-400"
      >
        <HelpCircle class="h-5 w-5" />
        Understanding This Report
      </CardTitle>
    </CardHeader>
    <CardContent class="space-y-4 text-sm text-blue-900 dark:text-blue-200">
      <p>
        <strong>What this shows:</strong>
        This report averages your glucose readings across all your site changes to
        reveal patterns in how your glucose control changes as your infusion site
        ages.
      </p>

      <div class="grid gap-4 @lg:grid-cols-2">
        <div>
          <p class="font-medium">Before Site Change (Left)</p>
          <p class="text-blue-700/80 dark:text-blue-300/80">
            Shows glucose patterns in the hours before you changed your site.
            Higher glucose here may indicate absorption issues with an aging
            site.
          </p>
        </div>
        <div>
          <p class="font-medium">After Site Change (Right)</p>
          <p class="text-blue-700/80 dark:text-blue-300/80">
            Shows glucose patterns after the fresh site is inserted. Watch for
            improvements in control indicating better insulin absorption.
          </p>
        </div>
      </div>

      <div class="rounded-md bg-blue-100/50 p-3 dark:bg-blue-900/30">
        <p class="flex items-center gap-2 font-medium">
          <Lightbulb class="h-4 w-4" />
          What to look for
        </p>
        <p class="text-blue-700/80 dark:text-blue-300/80">
          A consistent rise in the hours before site changes is a pattern worth
          discussing with your care team.
        </p>
      </div>
    </CardContent>
  </Card>

  <!-- Insights Card (when data is available) -->
  {#if analysis?.hasSufficientData && analysis?.summary}
    {@const summary = analysis.summary}
    {@const percentImprovement = summary.percentImprovement ?? 0}
    {@const tirBefore = summary.timeInRangeBeforeChange ?? 0}
    {@const tirAfter = summary.timeInRangeAfterChange ?? 0}
    <Card>
      <CardHeader>
        <CardTitle class="flex items-center gap-2">
          <Info class="h-5 w-5" />
          What the Averages Show
        </CardTitle>
      </CardHeader>
      <CardContent class="space-y-4">
        <div class="grid gap-4 @lg:grid-cols-2">
          <div class="flex items-start gap-3 rounded-lg bg-muted/50 p-4">
            {#if percentImprovement > 5}
              <TrendingDown class="h-5 w-5 shrink-0 text-muted-foreground" />
              <div>
                <p class="font-medium">Average glucose after a site change</p>
                <p class="text-sm text-muted-foreground">
                  {percentImprovement.toFixed(1)}% lower than in the hours
                  before.
                </p>
              </div>
            {:else if percentImprovement < -5}
              <TrendingUp class="h-5 w-5 shrink-0 text-muted-foreground" />
              <div>
                <p class="font-medium">Average glucose after a site change</p>
                <p class="text-sm text-muted-foreground">
                  {Math.abs(percentImprovement).toFixed(1)}% higher than in the
                  hours before. This can reflect insertion or site-location
                  factors.
                </p>
              </div>
            {:else}
              <Info class="h-5 w-5 shrink-0 text-muted-foreground" />
              <div>
                <p class="font-medium">Average glucose after a site change</p>
                <p class="text-sm text-muted-foreground">
                  Differs by {Math.abs(percentImprovement).toFixed(1)}% from the
                  hours before; differences under 5% are counted as no material
                  change.
                </p>
              </div>
            {/if}
          </div>

          <div class="flex items-start gap-3 rounded-lg bg-muted/50 p-4">
            <Target class="h-5 w-5 shrink-0 text-muted-foreground" />
            <div>
              <p class="font-medium">Time in range around a site change</p>
              <p class="text-sm text-muted-foreground">
                {tirBefore.toFixed(0)}% before, {tirAfter.toFixed(0)}% after.
              </p>
            </div>
          </div>
        </div>
        <p class="text-xs text-muted-foreground">
          These are averages across {analysis.siteChangeCount ?? 0} site changes
          in this window, not a per-change result. Discuss any patterns with your
          care team.
        </p>
      </CardContent>
    </Card>
  {/if}
</div>
{/if}
