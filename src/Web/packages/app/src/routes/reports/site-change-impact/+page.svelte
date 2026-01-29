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
  import {
    Calendar,
    Info,
    TrendingUp,
    TrendingDown,
    ArrowLeft,
    Printer,
    HelpCircle,
    Clock,
    RefreshCw,
  } from "lucide-svelte";
  import SiteChangeIcon from "$lib/components/icons/SiteChangeIcon.svelte";
  import SiteChangeImpactChart from "$lib/components/reports/SiteChangeImpactChart.svelte";
  import { getSiteChangeImpact } from "$lib/data/reports.remote";
  import { requireDateParamsContext } from "$lib/hooks/date-params.svelte";
  import { resource } from "runed";

  // Get shared date params from context (set by reports layout)
  // Default: 30 days to capture multiple site changes for meaningful analysis
  const reportsParams = requireDateParamsContext(30);

  // Use resource for controlled reactivity - prevents flickering by avoiding query recreation
  const siteChangeResource = resource(
    () => reportsParams.dateRangeInput,
    async (dateRangeInput) => {
      return await getSiteChangeImpact(dateRangeInput);
    },
    { debounce: 100 }
  );

  const isLoading = $derived(siteChangeResource.loading);
  const queryData = $derived(siteChangeResource.current);
  const analysis = $derived(queryData?.analysis ?? null);
  const dateRange = $derived(
    queryData?.dateRange ?? {
      from: new Date().toISOString(),
      to: new Date().toISOString(),
    }
  );

  // Helper dates
  const startDate = $derived(new Date(dateRange.from));
  const endDate = $derived(new Date(dateRange.to));
  const dayCount = $derived(
    Math.max(
      1,
      Math.round(
        (endDate.getTime() - startDate.getTime()) / (1000 * 60 * 60 * 24)
      )
    )
  );

  // Format date for display
  function formatDate(date: Date): string {
    return date.toLocaleDateString("en-US", {
      month: "short",
      day: "numeric",
      year: "numeric",
    });
  }
</script>

<svelte:head>
  <title>Site Change Impact - Nocturne Reports</title>
  <meta
    name="description"
    content="Analyze how pump site changes affect your glucose control"
  />
</svelte:head>

<div class="container mx-auto max-w-7xl space-y-8 px-4 py-6">
  <!-- Header -->
  <div class="space-y-4">
    <div class="flex flex-wrap items-center justify-between gap-4">
      <div>
        <h1 class="flex items-center gap-3 text-3xl font-bold">
          <SiteChangeIcon class="h-8 w-8 text-rose-600" />
          Site Change Impact
        </h1>
        <p class="mt-1 text-muted-foreground">
          Analyze glucose patterns before and after pump site changes
        </p>
      </div>
      <div class="flex items-center gap-2">
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
          <div class="flex items-center gap-2 text-sm">
            <RefreshCw class="h-4 w-4 text-muted-foreground" />
            <span class="font-medium">{analysis.siteChangeCount}</span>
            <span class="text-muted-foreground">site changes analyzed</span>
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
        <SiteChangeImpactChart {analysis} />
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

      <div class="grid gap-4 md:grid-cols-2">
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
        <p class="font-medium">💡 Tip</p>
        <p class="text-blue-700/80 dark:text-blue-300/80">
          If you see consistently higher glucose before site changes, consider
          changing your site more frequently or investigating potential site
          issues.
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
          Key Insights
        </CardTitle>
      </CardHeader>
      <CardContent class="space-y-4">
        <div class="grid gap-4 md:grid-cols-2">
          {#if percentImprovement > 5}
            <div
              class="flex items-start gap-3 rounded-lg bg-green-50 p-4 dark:bg-green-950/30"
            >
              <TrendingDown class="h-5 w-5 shrink-0 text-green-600" />
              <div>
                <p class="font-medium text-green-800 dark:text-green-400">
                  Significant Improvement
                </p>
                <p class="text-sm text-green-700 dark:text-green-300">
                  Your glucose improves by {percentImprovement.toFixed(1)}%
                  after site changes. Consider more frequent changes if site age
                  affects control.
                </p>
              </div>
            </div>
          {:else if percentImprovement < -5}
            <div
              class="flex items-start gap-3 rounded-lg bg-amber-50 p-4 dark:bg-amber-950/30"
            >
              <TrendingUp class="h-5 w-5 shrink-0 text-amber-600" />
              <div>
                <p class="font-medium text-amber-800 dark:text-amber-400">
                  Post-Change Rise
                </p>
                <p class="text-sm text-amber-700 dark:text-amber-300">
                  Your glucose is {Math.abs(percentImprovement).toFixed(1)}%
                  higher after site changes. This might indicate insertion
                  issues or site location sensitivity.
                </p>
              </div>
            </div>
          {:else}
            <div
              class="flex items-start gap-3 rounded-lg bg-blue-50 p-4 dark:bg-blue-950/30"
            >
              <Info class="h-5 w-5 shrink-0 text-blue-600" />
              <div>
                <p class="font-medium text-blue-800 dark:text-blue-400">
                  Stable Control
                </p>
                <p class="text-sm text-blue-700 dark:text-blue-300">
                  Your glucose control is relatively stable around site changes.
                  Site age doesn't appear to significantly affect your control.
                </p>
              </div>
            </div>
          {/if}

          {#if tirAfter > tirBefore + 5}
            <div
              class="flex items-start gap-3 rounded-lg bg-green-50 p-4 dark:bg-green-950/30"
            >
              <TrendingUp class="h-5 w-5 shrink-0 text-green-600" />
              <div>
                <p class="font-medium text-green-800 dark:text-green-400">
                  TIR Improvement
                </p>
                <p class="text-sm text-green-700 dark:text-green-300">
                  Time in range improves from {tirBefore.toFixed(0)}% to {tirAfter.toFixed(
                    0
                  )}% after site changes.
                </p>
              </div>
            </div>
          {/if}
        </div>
      </CardContent>
    </Card>
  {/if}
</div>
