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
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import {
    Select,
    SelectContent,
    SelectItem,
    SelectTrigger,
  } from "$lib/components/ui/select";
  import { Separator } from "$lib/components/ui/separator";
  import {
    AlertTriangle,
    Search,
    Filter,
    ChevronLeft,
    ChevronRight,
    Info,
    Clock,
    Server,
    AlertCircle,
  } from "lucide-svelte";
  import { page } from "$app/state";
  import { goto } from "$app/navigation";
  import { getAnalyses } from "../data.remote";

  // Get filter params from URL
  const urlFilters = $derived({
    requestPath: page.url.searchParams.get("requestPath") || undefined,
    overallMatch: page.url.searchParams.get("overallMatch")
      ? parseInt(page.url.searchParams.get("overallMatch")!)
      : undefined,
    fromDate: page.url.searchParams.get("fromDate") || undefined,
    toDate: page.url.searchParams.get("toDate") || undefined,
    count: parseInt(page.url.searchParams.get("count") || "50", 10),
    skip: parseInt(page.url.searchParams.get("skip") || "0", 10),
  });

  // Fetch analyses using remote function
  const data = $derived(await getAnalyses(urlFilters));

  const { analyses, filters } = $derived(data);

  let searchPath = $state(filters.requestPath || "");
  let selectedMatch = $state(filters.overallMatch?.toString() || "");
  let fromDate = $state(filters.fromDate ? filters.fromDate.split("T")[0] : "");
  let toDate = $state(filters.toDate ? filters.toDate.split("T")[0] : "");

  function applyFilters() {
    const params = new URLSearchParams();
    if (searchPath) params.set("requestPath", searchPath);
    if (selectedMatch) params.set("overallMatch", selectedMatch);
    if (fromDate) params.set("fromDate", new Date(fromDate).toISOString());
    if (toDate) params.set("toDate", new Date(toDate).toISOString());
    params.set("count", filters.count.toString());
    params.set("skip", "0"); // Reset to first page when filtering

    goto(`/dashboard/analyses?${params.toString()}`);
  }

  function nextPage() {
    const params = new URLSearchParams(page.url.searchParams);
    params.set("skip", (filters.skip + filters.count).toString());
    goto(`/dashboard/analyses?${params.toString()}`);
  }

  function prevPage() {
    const params = new URLSearchParams(page.url.searchParams);
    params.set("skip", Math.max(0, filters.skip - filters.count).toString());
    goto(`/dashboard/analyses?${params.toString()}`);
  }

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

  function getMatchTypeColor(matchType: number): string {
    switch (matchType) {
      case 0:
        return "bg-green-100 text-green-800";
      case 1:
        return "bg-yellow-100 text-yellow-800";
      case 2:
        return "bg-orange-100 text-orange-800";
      case 3:
        return "bg-red-100 text-red-800";
      case 4:
      case 5:
      case 6:
        return "bg-purple-100 text-purple-800";
      case 7:
        return "bg-gray-100 text-gray-800";
      default:
        return "bg-gray-100 text-gray-800";
    }
  }
</script>

<svelte:head>
  <title>Discrepancy Analyses - Nocturne Dashboard</title>
  <meta
    name="description"
    content="Detailed view of compatibility discrepancy analyses"
  />
</svelte:head>

<div class="container mx-auto p-6 space-y-6">
  <!-- Header -->
  <div class="flex items-center justify-between">
    <div>
      <h1 class="text-3xl font-bold tracking-tight">Discrepancy Analyses</h1>
      <p class="text-muted-foreground">
        Detailed view of request/response compatibility analyses
      </p>
    </div>
    <Button variant="outline" href="/dashboard">← Back to Dashboard</Button>
  </div>

  <!-- Filters -->
  <Card>
    <CardHeader>
      <CardTitle class="flex items-center gap-2">
        <Filter class="h-5 w-5" />
        Filters
      </CardTitle>
    </CardHeader>
    <CardContent>
      <div class="grid gap-4 md:grid-cols-2 lg:grid-cols-4">
        <div class="space-y-2">
          <Label for="searchPath">Request Path</Label>
          <Input
            id="searchPath"
            placeholder="e.g. /api/v1/entries"
            bind:value={searchPath}
          />
        </div>

        <div class="space-y-2">
          <Label for="matchType">Match Type</Label>
          <Select type="single" bind:value={selectedMatch}>
            <SelectTrigger>
              <span data-slot="select-value">
                {selectedMatch !== ""
                  ? getMatchTypeDescription(Number(selectedMatch))
                  : "All match types"}
              </span>
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="">All match types</SelectItem>
              <SelectItem value="0">Perfect Match</SelectItem>
              <SelectItem value="1">Minor Differences</SelectItem>
              <SelectItem value="2">Major Differences</SelectItem>
              <SelectItem value="3">Critical Differences</SelectItem>
              <SelectItem value="4">Nightscout Missing</SelectItem>
              <SelectItem value="5">Nocturne Missing</SelectItem>
              <SelectItem value="6">Both Missing</SelectItem>
              <SelectItem value="7">Comparison Error</SelectItem>
            </SelectContent>
          </Select>
        </div>

        <div class="space-y-2">
          <Label for="fromDate">From Date</Label>
          <Input id="fromDate" type="date" bind:value={fromDate} />
        </div>

        <div class="space-y-2">
          <Label for="toDate">To Date</Label>
          <Input id="toDate" type="date" bind:value={toDate} />
        </div>
      </div>

      <div class="flex gap-2 mt-4">
        <Button onclick={applyFilters}>
          <Search class="h-4 w-4 mr-2" />
          Apply Filters
        </Button>
        <Button variant="outline" href="/dashboard/analyses">
          Clear Filters
        </Button>
      </div>
    </CardContent>
  </Card>

  <!-- Results -->
  <Card>
    <CardHeader>
      <CardTitle>Analysis Results</CardTitle>
      <CardDescription>
        Showing {analyses.length} results (starting from {filters.skip + 1})
      </CardDescription>
    </CardHeader>
    <CardContent>
      <div class="space-y-4">
        {#each analyses as analysis}
          <div class="border rounded-lg p-4 space-y-3">
            <!-- Header -->
            <div class="flex items-center justify-between">
              <div class="flex items-center gap-2">
                <code class="text-sm bg-muted px-2 py-1 rounded">
                  {analysis.requestMethod}
                </code>
                <span class="font-medium truncate">{analysis.requestPath}</span>
                <Badge
                  class="{getMatchTypeColor(analysis.overallMatch)} text-xs"
                >
                  {getMatchTypeDescription(analysis.overallMatch)}
                </Badge>
              </div>
              <div class="flex items-center gap-2">
                <Badge variant="outline" class="text-xs">
                  <Clock class="h-3 w-3 mr-1" />
                  {analysis.totalProcessingTimeMs}ms
                </Badge>
                <Button
                  variant="ghost"
                  size="sm"
                  href="/dashboard/analyses/{analysis.id}"
                >
                  <Info class="h-4 w-4" />
                </Button>
              </div>
            </div>

            <!-- Metadata -->
            <div class="text-sm text-muted-foreground">
              <div class="flex items-center gap-4">
                <span>ID: {analysis.correlationId}</span>
                <span>
                  Time: {new Date(analysis.analysisTimestamp).toLocaleString()}
                </span>
                {#if analysis.selectedResponseTarget}
                  <span>Selected: {analysis.selectedResponseTarget}</span>
                {/if}
              </div>
            </div>

            <!-- Status Codes -->
            {#if analysis.nightscoutStatusCode || analysis.nocturneStatusCode}
              <div class="flex gap-4 text-sm">
                <div class="flex items-center gap-2">
                  <Server class="h-4 w-4" />
                  <span>Nightscout:</span>
                  <Badge
                    variant="outline"
                    class={analysis.nightscoutStatusCode
                      ? analysis.nightscoutStatusCode >= 200 &&
                        analysis.nightscoutStatusCode < 300
                        ? "bg-green-100 text-green-800"
                        : "bg-red-100 text-red-800"
                      : "bg-gray-100 text-gray-800"}
                  >
                    {analysis.nightscoutStatusCode || "N/A"}
                  </Badge>
                </div>
                <div class="flex items-center gap-2">
                  <Server class="h-4 w-4" />
                  <span>Nocturne:</span>
                  <Badge
                    variant="outline"
                    class={analysis.nocturneStatusCode
                      ? analysis.nocturneStatusCode >= 200 &&
                        analysis.nocturneStatusCode < 300
                        ? "bg-green-100 text-green-800"
                        : "bg-red-100 text-red-800"
                      : "bg-gray-100 text-gray-800"}
                  >
                    {analysis.nocturneStatusCode || "N/A"}
                  </Badge>
                </div>
              </div>
            {/if}

            <!-- Discrepancy Counts -->
            <div class="flex gap-2">
              {#if analysis.criticalDiscrepancyCount > 0}
                <Badge class="bg-red-100 text-red-800 text-xs">
                  <AlertTriangle class="h-3 w-3 mr-1" />
                  {analysis.criticalDiscrepancyCount} Critical
                </Badge>
              {/if}
              {#if analysis.majorDiscrepancyCount > 0}
                <Badge class="bg-orange-100 text-orange-800 text-xs">
                  <AlertCircle class="h-3 w-3 mr-1" />
                  {analysis.majorDiscrepancyCount} Major
                </Badge>
              {/if}
              {#if analysis.minorDiscrepancyCount > 0}
                <Badge class="bg-yellow-100 text-yellow-800 text-xs">
                  {analysis.minorDiscrepancyCount} Minor
                </Badge>
              {/if}
              {#if analysis.criticalDiscrepancyCount === 0 && analysis.majorDiscrepancyCount === 0 && analysis.minorDiscrepancyCount === 0}
                <Badge class="bg-green-100 text-green-800 text-xs">
                  No Issues
                </Badge>
              {/if}
            </div>

            <!-- Summary -->
            {#if analysis.summary}
              <p class="text-sm text-muted-foreground">
                {analysis.summary}
              </p>
            {/if}

            <!-- Error Message -->
            {#if analysis.errorMessage}
              <div class="bg-red-50 border border-red-200 rounded p-2">
                <p class="text-sm text-red-800">
                  <AlertTriangle class="h-4 w-4 inline mr-2" />
                  {analysis.errorMessage}
                </p>
              </div>
            {/if}
          </div>
        {:else}
          <div class="text-center py-8">
            <p class="text-muted-foreground">
              No analyses found matching the current filters.
            </p>
          </div>
        {/each}
      </div>

      <!-- Pagination -->
      {#if analyses.length > 0}
        <Separator class="my-6" />
        <div class="flex items-center justify-between">
          <Button
            variant="outline"
            size="sm"
            disabled={filters.skip === 0}
            onclick={prevPage}
          >
            <ChevronLeft class="h-4 w-4 mr-2" />
            Previous
          </Button>

          <span class="text-sm text-muted-foreground">
            Showing {filters.skip + 1}-{filters.skip + analyses.length}
          </span>

          <Button
            variant="outline"
            size="sm"
            disabled={analyses.length < filters.count}
            onclick={nextPage}
          >
            Next
            <ChevronRight class="h-4 w-4 ml-2" />
          </Button>
        </div>
      {/if}
    </CardContent>
  </Card>
</div>
