<script lang="ts">
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { Badge } from "$lib/components/ui/badge";
  import { Button } from "$lib/components/ui/button";
  import { Separator } from "$lib/components/ui/separator";
  import {
    Activity,
    AlertTriangle,
    CheckCircle,
    TrendingUp,
    TrendingDown,
    Clock,
    Server,
    BarChart3,
    AlertCircle,
    Info,
    Settings,
    RefreshCw,
  } from "lucide-svelte";
  import { page } from "$app/state";
  import { goto } from "$app/navigation";
  import { getDashboardData } from "./data.remote";

  // Get filter params from URL
  const urlFilters = $derived({
    fromDate: page.url.searchParams.get("fromDate") || undefined,
    toDate: page.url.searchParams.get("toDate") || undefined,
  });

  // Fetch dashboard data using remote function
  const data = $derived(await getDashboardData(urlFilters));

  const { metrics, endpoints, analyses, status, filters } = $derived(data);

  // Helper function to get status color
  function getStatusColor(healthStatus: string): string {
    switch (healthStatus.toLowerCase()) {
      case "excellent":
        return "bg-green-500";
      case "good":
        return "bg-blue-500";
      case "fair":
        return "bg-yellow-500";
      case "poor":
        return "bg-orange-500";
      case "critical":
        return "bg-red-500";
      default:
        return "bg-gray-500";
    }
  }

  // Helper function to get match type description
  function getMatchTypeDescription(matchType: number): string {
    switch (matchType) {
      case 0:
        return "Perfect Match";
      case 1:
        return "Minor Differences";
      case 2:
        return "Major Differences";
      case 3:
        return "Critical Differences";
      case 4:
        return "Nightscout Missing";
      case 5:
        return "Nocturne Missing";
      case 6:
        return "Both Missing";
      case 7:
        return "Comparison Error";
      default:
        return "Unknown";
    }
  }

  // Helper function to get severity badge color
  function getSeverityColor(severity: number): string {
    switch (severity) {
      case 0:
        return "bg-yellow-100 text-yellow-800";
      case 1:
        return "bg-orange-100 text-orange-800";
      case 2:
        return "bg-red-100 text-red-800";
      default:
        return "bg-gray-100 text-gray-800";
    }
  }

  function refreshData() {
    goto(page.url.pathname + page.url.search, { invalidateAll: true });
  }
</script>

<svelte:head>
  <title>Compatibility Dashboard - Nocturne</title>
  <meta
    name="description"
    content="Monitor Nightscout/Nocturne compatibility in real-time"
  />
</svelte:head>

<div class="container mx-auto p-6 space-y-6">
  <!-- Header -->
  <div class="flex items-center justify-between">
    <div>
      <h1 class="text-3xl font-bold tracking-tight">Compatibility Dashboard</h1>
      <p class="text-muted-foreground">
        Monitor Nightscout/Nocturne API compatibility in real-time
      </p>
    </div>
    <div class="flex items-center gap-2">
      <Button variant="outline" size="sm" on:click={refreshData}>
        <RefreshCw class="h-4 w-4 mr-2" />
        Refresh
      </Button>
      <Button variant="outline" size="sm" href="/dashboard/settings">
        <Settings class="h-4 w-4 mr-2" />
        Settings
      </Button>
    </div>
  </div>

  <!-- Overall Status Cards -->
  <div class="grid gap-4 md:grid-cols-2 lg:grid-cols-4">
    <!-- Overall Score -->
    <Card>
      <CardHeader
        class="flex flex-row items-center justify-between space-y-0 pb-2"
      >
        <CardTitle class="text-sm font-medium">Compatibility Score</CardTitle>
        <CheckCircle class="h-4 w-4 text-muted-foreground" />
      </CardHeader>
      <CardContent>
        <div class="text-2xl font-bold">
          {metrics.compatibilityScore.toFixed(1)}%
        </div>
        <p class="text-xs text-muted-foreground">
          <Badge
            class="{getStatusColor(status.healthStatus)} text-white text-xs"
          >
            {status.healthStatus}
          </Badge>
        </p>
      </CardContent>
    </Card>

    <!-- Total Requests -->
    <Card>
      <CardHeader
        class="flex flex-row items-center justify-between space-y-0 pb-2"
      >
        <CardTitle class="text-sm font-medium">Total Requests</CardTitle>
        <Activity class="h-4 w-4 text-muted-foreground" />
      </CardHeader>
      <CardContent>
        <div class="text-2xl font-bold">
          {metrics.totalRequests.toLocaleString()}
        </div>
        <p class="text-xs text-muted-foreground">In selected period</p>
      </CardContent>
    </Card>

    <!-- Critical Issues -->
    <Card>
      <CardHeader
        class="flex flex-row items-center justify-between space-y-0 pb-2"
      >
        <CardTitle class="text-sm font-medium">Critical Issues</CardTitle>
        <AlertTriangle class="h-4 w-4 text-muted-foreground" />
      </CardHeader>
      <CardContent>
        <div class="text-2xl font-bold text-red-600">
          {status.criticalIssues}
        </div>
        <p class="text-xs text-muted-foreground">Require immediate attention</p>
      </CardContent>
    </Card>

    <!-- Response Time Comparison -->
    <Card>
      <CardHeader
        class="flex flex-row items-center justify-between space-y-0 pb-2"
      >
        <CardTitle class="text-sm font-medium">Avg Response Time</CardTitle>
        <Clock class="h-4 w-4 text-muted-foreground" />
      </CardHeader>
      <CardContent>
        <div class="text-2xl font-bold">
          {Math.round(metrics.averageNocturneResponseTime)}ms
        </div>
        <p class="text-xs text-muted-foreground">
          vs {Math.round(metrics.averageNightscoutResponseTime)}ms Nightscout
        </p>
      </CardContent>
    </Card>
  </div>

  <!-- Detailed Metrics -->
  <div class="grid gap-6 md:grid-cols-2">
    <!-- Match Type Breakdown -->
    <Card>
      <CardHeader>
        <CardTitle>Response Match Breakdown</CardTitle>
        <CardDescription>
          Distribution of response comparison results
        </CardDescription>
      </CardHeader>
      <CardContent class="space-y-4">
        <div class="space-y-2">
          <div class="flex justify-between items-center">
            <span class="text-sm">Perfect Matches</span>
            <Badge class="bg-green-100 text-green-800">
              {metrics.perfectMatches} ({(
                (metrics.perfectMatches / metrics.totalRequests) *
                100
              ).toFixed(1)}%)
            </Badge>
          </div>
          <div class="flex justify-between items-center">
            <span class="text-sm">Minor Differences</span>
            <Badge class="bg-yellow-100 text-yellow-800">
              {metrics.minorDifferences} ({(
                (metrics.minorDifferences / metrics.totalRequests) *
                100
              ).toFixed(1)}%)
            </Badge>
          </div>
          <div class="flex justify-between items-center">
            <span class="text-sm">Major Differences</span>
            <Badge class="bg-orange-100 text-orange-800">
              {metrics.majorDifferences} ({(
                (metrics.majorDifferences / metrics.totalRequests) *
                100
              ).toFixed(1)}%)
            </Badge>
          </div>
          <div class="flex justify-between items-center">
            <span class="text-sm">Critical Differences</span>
            <Badge class="bg-red-100 text-red-800">
              {metrics.criticalDifferences} ({(
                (metrics.criticalDifferences / metrics.totalRequests) *
                100
              ).toFixed(1)}%)
            </Badge>
          </div>
        </div>
      </CardContent>
    </Card>

    <!-- Top Problematic Endpoints -->
    <Card>
      <CardHeader>
        <CardTitle>Top Problematic Endpoints</CardTitle>
        <CardDescription>
          Endpoints with the most compatibility issues
        </CardDescription>
      </CardHeader>
      <CardContent>
        <div class="space-y-3">
          {#each endpoints
            .slice(0, 5)
            .sort((a, b) => b.criticalDifferences + b.majorDifferences - (a.criticalDifferences + a.majorDifferences)) as endpoint}
            <div
              class="flex items-center justify-between p-2 rounded-lg border"
            >
              <div class="flex-1">
                <div class="font-medium text-sm truncate">
                  {endpoint.endpoint}
                </div>
                <div class="text-xs text-muted-foreground">
                  {endpoint.totalRequests} requests • {endpoint.compatibilityScore.toFixed(
                    1
                  )}% compatible
                </div>
              </div>
              <div class="flex gap-1">
                {#if endpoint.criticalDifferences > 0}
                  <Badge class="bg-red-100 text-red-800 text-xs">
                    {endpoint.criticalDifferences}
                  </Badge>
                {/if}
                {#if endpoint.majorDifferences > 0}
                  <Badge class="bg-orange-100 text-orange-800 text-xs">
                    {endpoint.majorDifferences}
                  </Badge>
                {/if}
              </div>
            </div>
          {:else}
            <p class="text-sm text-muted-foreground">
              No endpoints with issues found
            </p>
          {/each}
        </div>
        <Separator class="my-4" />
        <Button
          variant="outline"
          size="sm"
          href="/dashboard/endpoints"
          class="w-full"
        >
          <BarChart3 class="h-4 w-4 mr-2" />
          View All Endpoints
        </Button>
      </CardContent>
    </Card>
  </div>

  <!-- Recent Analyses -->
  <Card>
    <CardHeader>
      <CardTitle>Recent Discrepancy Analyses</CardTitle>
      <CardDescription>Latest compatibility analysis results</CardDescription>
    </CardHeader>
    <CardContent>
      <div class="space-y-3">
        {#each analyses.slice(0, 10) as analysis}
          <div class="flex items-center justify-between p-3 rounded-lg border">
            <div class="flex-1">
              <div class="flex items-center gap-2 mb-1">
                <code class="text-xs bg-muted px-1 py-0.5 rounded">
                  {analysis.requestMethod}
                </code>
                <span class="font-medium text-sm truncate">
                  {analysis.requestPath}
                </span>
                <Badge
                  class="text-xs {getSeverityColor(analysis.overallMatch)}"
                >
                  {getMatchTypeDescription(analysis.overallMatch)}
                </Badge>
              </div>
              <div class="text-xs text-muted-foreground">
                {new Date(analysis.analysisTimestamp).toLocaleString()} •
                {analysis.totalProcessingTimeMs}ms •
                {analysis.criticalDiscrepancyCount +
                  analysis.majorDiscrepancyCount +
                  analysis.minorDiscrepancyCount} discrepancies
              </div>
            </div>
            <div class="flex items-center gap-2">
              {#if analysis.criticalDiscrepancyCount > 0}
                <Badge class="bg-red-100 text-red-800 text-xs">
                  <AlertTriangle class="h-3 w-3 mr-1" />
                  {analysis.criticalDiscrepancyCount}
                </Badge>
              {/if}
              {#if analysis.majorDiscrepancyCount > 0}
                <Badge class="bg-orange-100 text-orange-800 text-xs">
                  {analysis.majorDiscrepancyCount}
                </Badge>
              {/if}
              {#if analysis.minorDiscrepancyCount > 0}
                <Badge class="bg-yellow-100 text-yellow-800 text-xs">
                  {analysis.minorDiscrepancyCount}
                </Badge>
              {/if}
              <Button
                variant="ghost"
                size="sm"
                href="/dashboard/analyses/{analysis.id}"
              >
                <Info class="h-4 w-4" />
              </Button>
            </div>
          </div>
        {:else}
          <p class="text-sm text-muted-foreground">No recent analyses found</p>
        {/each}
      </div>
      <Separator class="my-4" />
      <Button
        variant="outline"
        size="sm"
        href="/dashboard/analyses"
        class="w-full"
      >
        View All Analyses
      </Button>
    </CardContent>
  </Card>
</div>
