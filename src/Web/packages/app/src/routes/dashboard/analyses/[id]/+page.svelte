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
    Tabs,
    TabsContent,
    TabsList,
    TabsTrigger,
  } from "$lib/components/ui/tabs";
  import {
    AlertTriangle,
    Clock,
    Server,
    AlertCircle,
    Info,
    CheckCircle,
    Copy,
    ArrowLeft,
  } from "lucide-svelte";
  import { page } from "$app/state";
  import { getAnalysisById } from "../../data.remote";

  // Get ID from route params
  const analysisId = $derived(page.params.id);

  // Fetch analysis using remote function
  const data = $derived(await getAnalysisById(analysisId));

  const { analysis } = $derived(data);

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

  function getDiscrepancyTypeDescription(type: number): string {
    switch (type) {
      case 0:
        return "Status Code";
      case 1:
        return "Header";
      case 2:
        return "Content Type";
      case 3:
        return "Body";
      case 4:
        return "JSON Structure";
      case 5:
        return "String Value";
      case 6:
        return "Numeric Value";
      case 7:
        return "Timestamp";
      case 8:
        return "Array Length";
      case 9:
        return "Performance";
      default:
        return "Unknown";
    }
  }

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

  function getSeverityDescription(severity: number): string {
    switch (severity) {
      case 0:
        return "Minor";
      case 1:
        return "Major";
      case 2:
        return "Critical";
      default:
        return "Unknown";
    }
  }

  function copyToClipboard(text: string) {
    navigator.clipboard.writeText(text);
  }
</script>

<svelte:head>
  <title>Analysis {analysis.correlationId} - Nocturne Dashboard</title>
  <meta name="description" content="Detailed view of discrepancy analysis" />
</svelte:head>

<div class="container mx-auto p-6 space-y-6">
  <!-- Header -->
  <div class="flex items-center justify-between">
    <div>
      <div class="flex items-center gap-2 mb-2">
        <Button variant="ghost" size="sm" href="/dashboard/analyses">
          <ArrowLeft class="h-4 w-4 mr-2" />
          Back to Analyses
        </Button>
      </div>
      <h1 class="text-3xl font-bold tracking-tight">Analysis Details</h1>
      <p class="text-muted-foreground">
        Correlation ID: {analysis.correlationId}
      </p>
    </div>
  </div>

  <!-- Overview Card -->
  <Card>
    <CardHeader>
      <CardTitle class="flex items-center justify-between">
        <div class="flex items-center gap-2">
          <code class="bg-muted px-2 py-1 rounded text-sm">
            {analysis.requestMethod}
          </code>
          <span>{analysis.requestPath}</span>
        </div>
        <Badge class={getMatchTypeColor(analysis.overallMatch)}>
          {getMatchTypeDescription(analysis.overallMatch)}
        </Badge>
      </CardTitle>
      <CardDescription>
        Analyzed on {new Date(analysis.analysisTimestamp).toLocaleString()}
      </CardDescription>
    </CardHeader>
    <CardContent class="space-y-4">
      <!-- Basic Info -->
      <div class="grid gap-4 md:grid-cols-2 lg:grid-cols-4">
        <div>
          <div class="text-sm font-medium">Processing Time</div>
          <div class="text-lg font-bold text-blue-600">
            {analysis.totalProcessingTimeMs}ms
          </div>
        </div>
        <div>
          <div class="text-sm font-medium">Status Match</div>
          <div
            class="{analysis.statusCodeMatch
              ? 'text-green-600'
              : 'text-red-600'} font-bold"
          >
            {analysis.statusCodeMatch ? "Yes" : "No"}
          </div>
        </div>
        <div>
          <div class="text-sm font-medium">Body Match</div>
          <div
            class="{analysis.bodyMatch
              ? 'text-green-600'
              : 'text-red-600'} font-bold"
          >
            {analysis.bodyMatch ? "Yes" : "No"}
          </div>
        </div>
        <div>
          <div class="text-sm font-medium">Total Discrepancies</div>
          <div class="text-lg font-bold text-orange-600">
            {analysis.criticalDiscrepancyCount +
              analysis.majorDiscrepancyCount +
              analysis.minorDiscrepancyCount}
          </div>
        </div>
      </div>

      <!-- Status Codes -->
      {#if analysis.nightscoutStatusCode || analysis.nocturneStatusCode}
        <Separator />
        <div>
          <h3 class="font-semibold mb-2">Response Status Codes</h3>
          <div class="flex gap-4">
            <div class="flex items-center gap-2">
              <Server class="h-4 w-4" />
              <span class="text-sm">Nightscout:</span>
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
              <span class="text-sm">Nocturne:</span>
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
        </div>
      {/if}

      <!-- Response Times -->
      {#if analysis.nightscoutResponseTimeMs || analysis.nocturneResponseTimeMs}
        <Separator />
        <div>
          <h3 class="font-semibold mb-2">Response Times</h3>
          <div class="flex gap-4">
            <div class="flex items-center gap-2">
              <Clock class="h-4 w-4" />
              <span class="text-sm">Nightscout:</span>
              <Badge variant="outline">
                {analysis.nightscoutResponseTimeMs || "N/A"}ms
              </Badge>
            </div>
            <div class="flex items-center gap-2">
              <Clock class="h-4 w-4" />
              <span class="text-sm">Nocturne:</span>
              <Badge variant="outline">
                {analysis.nocturneResponseTimeMs || "N/A"}ms
              </Badge>
            </div>
          </div>
        </div>
      {/if}

      <!-- Selection Info -->
      {#if analysis.selectedResponseTarget}
        <Separator />
        <div>
          <h3 class="font-semibold mb-2">Response Selection</h3>
          <div class="space-y-2">
            <div class="flex items-center gap-2">
              <span class="text-sm">Selected Response:</span>
              <Badge class="bg-blue-100 text-blue-800">
                {analysis.selectedResponseTarget}
              </Badge>
            </div>
            {#if analysis.selectionReason}
              <p class="text-sm text-muted-foreground">
                {analysis.selectionReason}
              </p>
            {/if}
          </div>
        </div>
      {/if}

      <!-- Summary -->
      {#if analysis.summary}
        <Separator />
        <div>
          <h3 class="font-semibold mb-2">Summary</h3>
          <p class="text-sm">{analysis.summary}</p>
        </div>
      {/if}

      <!-- Error Message -->
      {#if analysis.errorMessage}
        <Separator />
        <div class="bg-red-50 border border-red-200 rounded p-3">
          <h3 class="font-semibold text-red-800 mb-2 flex items-center gap-2">
            <AlertTriangle class="h-4 w-4" />
            Error
          </h3>
          <p class="text-sm text-red-700">{analysis.errorMessage}</p>
        </div>
      {/if}
    </CardContent>
  </Card>

  <!-- Discrepancies -->
  {#if analysis.discrepancies && analysis.discrepancies.length > 0}
    <Card>
      <CardHeader>
        <CardTitle class="flex items-center gap-2">
          <AlertCircle class="h-5 w-5" />
          Discrepancies ({analysis.discrepancies.length})
        </CardTitle>
        <CardDescription>
          Detailed breakdown of differences found between responses
        </CardDescription>
      </CardHeader>
      <CardContent>
        <div class="space-y-4">
          {#each analysis.discrepancies as discrepancy}
            <div class="border rounded-lg p-4 space-y-3">
              <!-- Header -->
              <div class="flex items-center justify-between">
                <div class="flex items-center gap-2">
                  <Badge
                    class="{getSeverityColor(discrepancy.severity)} text-xs"
                  >
                    {getSeverityDescription(discrepancy.severity)}
                  </Badge>
                  <Badge variant="outline" class="text-xs">
                    {getDiscrepancyTypeDescription(discrepancy.discrepancyType)}
                  </Badge>
                  <code class="text-sm bg-muted px-2 py-1 rounded">
                    {discrepancy.field}
                  </code>
                </div>
                <span class="text-xs text-muted-foreground">
                  {new Date(discrepancy.recordedAt).toLocaleString()}
                </span>
              </div>

              <!-- Description -->
              <p class="text-sm">{discrepancy.description}</p>

              <!-- Values Comparison -->
              <div class="grid gap-4 md:grid-cols-2">
                <div>
                  <div class="flex items-center justify-between mb-1">
                    <h4 class="text-sm font-semibold text-purple-700">
                      Nightscout Value
                    </h4>
                    <Button
                      variant="ghost"
                      size="sm"
                      on:click={() =>
                        copyToClipboard(discrepancy.nightscoutValue)}
                    >
                      <Copy class="h-3 w-3" />
                    </Button>
                  </div>
                  <div
                    class="bg-purple-50 border border-purple-200 rounded p-3"
                  >
                    <pre
                      class="text-xs overflow-x-auto">{discrepancy.nightscoutValue}</pre>
                  </div>
                </div>
                <div>
                  <div class="flex items-center justify-between mb-1">
                    <h4 class="text-sm font-semibold text-green-700">
                      Nocturne Value
                    </h4>
                    <Button
                      variant="ghost"
                      size="sm"
                      on:click={() =>
                        copyToClipboard(discrepancy.nocturneValue)}
                    >
                      <Copy class="h-3 w-3" />
                    </Button>
                  </div>
                  <div class="bg-green-50 border border-green-200 rounded p-3">
                    <pre
                      class="text-xs overflow-x-auto">{discrepancy.nocturneValue}</pre>
                  </div>
                </div>
              </div>
            </div>
          {/each}
        </div>
      </CardContent>
    </Card>
  {:else}
    <Card>
      <CardHeader>
        <CardTitle class="flex items-center gap-2">
          <CheckCircle class="h-5 w-5 text-green-500" />
          No Discrepancies Found
        </CardTitle>
        <CardDescription>
          The responses matched perfectly with no differences detected.
        </CardDescription>
      </CardHeader>
    </Card>
  {/if}
</div>
