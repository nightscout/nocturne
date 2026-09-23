<script lang="ts">
  import { page } from "$app/state";
  import { goto } from "$app/navigation";
  import { getAnalysisDetail } from "../data.remote";
  import { formatDateTimeCompact } from "$lib/utils/formatting";
  import { formatElapsedMs } from "$lib/utils/duration";
  import { getMatchTypeDisplay } from "$lib/utils/compatibility-match";
  import { Button } from "$lib/components/ui/button";

  // Get ID from route params (guaranteed to exist in [id] route)
  const analysisId = $derived(page.params.id ?? "");

  // Fetch analysis data using remote function
  const analysisQuery = $derived(getAnalysisDetail(analysisId));

  const analysis = $derived(analysisQuery.current?.analysis ?? {});

  // Helper to get discrepancy type display
  function getDiscrepancyTypeDisplay(type: number | undefined) {
    if (type === undefined) return "Unknown";
    const types = [
      "Status Code",
      "Header",
      "Content Type",
      "Body",
      "JSON Structure",
      "String Value",
      "Numeric Value",
      "Timestamp",
      "Array Length",
      "Performance",
    ];
    return types[type] || "Unknown";
  }


  // Group discrepancies by severity
  const discrepanciesBySeverity = $derived({
    critical:
      analysis.discrepancies?.filter((d: any) => d.severity === 2) || [],
    major: analysis.discrepancies?.filter((d: any) => d.severity === 1) || [],
    minor: analysis.discrepancies?.filter((d: any) => d.severity === 0) || [],
  });

  const matchType = $derived(getMatchTypeDisplay(analysis.overallMatch));
</script>

<div class="@container container mx-auto p-6 space-y-6">
  <!-- Header with Back Button -->
  <div class="flex items-center gap-4">
    <Button variant="secondary" onclick={() => goto("/compatibility")}>
      ← Back
    </Button>
    <h1 class="text-3xl font-bold">Request Analysis Detail</h1>
  </div>

  <!-- Overview Card -->
  <div class="bg-card rounded-lg shadow p-6">
    <h2 class="text-xl font-semibold mb-4">Overview</h2>
    <div class="grid grid-cols-1 @lg:grid-cols-2 @3xl:grid-cols-3 gap-4">
      <div>
        <p class="text-sm text-muted-foreground">Correlation ID</p>
        <p class="font-mono text-sm">{analysis.correlationId}</p>
      </div>
      <div>
        <p class="text-sm text-muted-foreground">Timestamp</p>
        <p class="text-sm">{formatDateTimeCompact(analysis.analysisTimestamp)}</p>
      </div>
      <div>
        <p class="text-sm text-muted-foreground">Overall Match</p>
        <span
          class="inline-block px-3 py-1 text-sm font-semibold rounded-full {matchType.class}"
        >
          {matchType.longLabel}
        </span>
      </div>
      <div>
        <p class="text-sm text-muted-foreground">Request Method</p>
        <p class="font-mono font-semibold">{analysis.requestMethod}</p>
      </div>
      <div class="@lg:col-span-2">
        <p class="text-sm text-muted-foreground">Request Path</p>
        <p class="font-mono text-sm break-all">{analysis.requestPath}</p>
      </div>
    </div>
  </div>

  <!-- Status Codes & Response Times -->
  <div class="grid grid-cols-1 @lg:grid-cols-2 gap-6">
    <!-- Status Codes -->
    <div class="bg-card rounded-lg shadow p-6">
      <h2 class="text-xl font-semibold mb-4">Status Codes</h2>
      <div class="space-y-3">
        <div class="flex justify-between items-center">
          <span class="text-muted-foreground">Nightscout</span>
          <span
            class="font-mono font-semibold {analysis.statusCodeMatch
              ? 'text-success'
              : 'text-destructive'}"
          >
            {analysis.nightscoutStatusCode || "N/A"}
          </span>
        </div>
        <div class="flex justify-between items-center">
          <span class="text-muted-foreground">Nocturne</span>
          <span
            class="font-mono font-semibold {analysis.statusCodeMatch
              ? 'text-success'
              : 'text-destructive'}"
          >
            {analysis.nocturneStatusCode || "N/A"}
          </span>
        </div>
        <div class="pt-2 border-t">
          <span class="text-muted-foreground">Match</span>
          <span
            class="ml-2 {analysis.statusCodeMatch
              ? 'text-success'
              : 'text-destructive'} font-semibold"
          >
            {analysis.statusCodeMatch ? "✓ Matched" : "✗ Different"}
          </span>
        </div>
      </div>
    </div>

    <!-- Response Times -->
    <div class="bg-card rounded-lg shadow p-6">
      <h2 class="text-xl font-semibold mb-4">Response Times</h2>
      <div class="space-y-3">
        <div class="flex justify-between items-center">
          <span class="text-muted-foreground">Nightscout</span>
          <span class="font-mono font-semibold">
            {formatElapsedMs(analysis.nightscoutResponseTimeMs || 0)}
          </span>
        </div>
        <div class="flex justify-between items-center">
          <span class="text-muted-foreground">Nocturne</span>
          <span class="font-mono font-semibold">
            {formatElapsedMs(analysis.nocturneResponseTimeMs || 0)}
          </span>
        </div>
        <div class="flex justify-between items-center">
          <span class="text-muted-foreground">Total Processing</span>
          <span class="font-mono font-semibold">
            {formatElapsedMs(analysis.totalProcessingTimeMs || 0)}
          </span>
        </div>
        {#if analysis.nightscoutResponseTimeMs && analysis.nocturneResponseTimeMs}
          {@const diff =
            analysis.nocturneResponseTimeMs - analysis.nightscoutResponseTimeMs}
          {@const faster = diff < 0 ? "Nocturne" : "Nightscout"}
          <div class="pt-2 border-t">
            <span class="text-muted-foreground">Faster</span>
            <span class="ml-2 font-semibold text-info">
              {faster} by {formatElapsedMs(Math.abs(diff))}
            </span>
          </div>
        {/if}
      </div>
    </div>
  </div>

  <!-- Selection Details -->
  {#if analysis.selectedResponseTarget}
    <div class="bg-card rounded-lg shadow p-6">
      <h2 class="text-xl font-semibold mb-4">Response Selection</h2>
      <div class="grid grid-cols-1 @lg:grid-cols-2 gap-4">
        <div>
          <p class="text-sm text-muted-foreground">
            Selected Target
          </p>
          <p class="font-semibold">{analysis.selectedResponseTarget}</p>
        </div>
        <div>
          <p class="text-sm text-muted-foreground">Reason</p>
          <p class="text-sm">{analysis.selectionReason || "N/A"}</p>
        </div>
      </div>
    </div>
  {/if}

  <!-- Summary -->
  {#if analysis.summary}
    <div class="bg-card rounded-lg shadow p-6">
      <h2 class="text-xl font-semibold mb-4">Summary</h2>
      <p class="text-sm whitespace-pre-wrap">{analysis.summary}</p>
    </div>
  {/if}

  <!-- Discrepancies -->
  {#if analysis.discrepancies && analysis.discrepancies.length > 0}
    <div class="bg-card rounded-lg shadow p-6">
      <h2 class="text-xl font-semibold mb-4">
        Discrepancies ({analysis.discrepancies.length})
      </h2>

      <!-- Critical Discrepancies -->
      {#if discrepanciesBySeverity.critical.length > 0}
        <div class="mb-6">
          <h3 class="text-lg font-semibold text-destructive mb-3">
            Critical ({discrepanciesBySeverity.critical.length})
          </h3>
          <div class="space-y-3">
            {#each discrepanciesBySeverity.critical as disc (disc.id)}
              <div
                class="bg-destructive/10 rounded-lg p-4 border border-destructive/30"
              >
                <div class="flex justify-between items-start mb-2">
                  <div>
                    <span class="font-semibold">
                      {getDiscrepancyTypeDisplay(disc.discrepancyType)}
                    </span>
                    <span class="text-sm text-muted-foreground ml-2">
                      in field: <span class="font-mono">{disc.field}</span>
                    </span>
                  </div>
                  <span
                    class="px-2 py-1 text-xs font-semibold rounded-full bg-destructive/10 text-destructive"
                  >
                    Critical
                  </span>
                </div>
                <p class="text-sm mb-2">{disc.description}</p>
                <div class="grid grid-cols-1 @lg:grid-cols-2 gap-2 mt-2">
                  <div>
                    <p class="text-xs text-muted-foreground">
                      Nightscout Value
                    </p>
                    <p
                      class="font-mono text-sm bg-muted p-2 rounded break-all"
                    >
                      {disc.nightscoutValue || "null"}
                    </p>
                  </div>
                  <div>
                    <p class="text-xs text-muted-foreground">
                      Nocturne Value
                    </p>
                    <p
                      class="font-mono text-sm bg-muted p-2 rounded break-all"
                    >
                      {disc.nocturneValue || "null"}
                    </p>
                  </div>
                </div>
              </div>
            {/each}
          </div>
        </div>
      {/if}

      <!-- Major Discrepancies -->
      {#if discrepanciesBySeverity.major.length > 0}
        <div class="mb-6">
          <h3 class="text-lg font-semibold text-warning mb-3">
            Major ({discrepanciesBySeverity.major.length})
          </h3>
          <div class="space-y-3">
            {#each discrepanciesBySeverity.major as disc (disc.id)}
              <div
                class="bg-warning/10 rounded-lg p-4 border border-warning/30"
              >
                <div class="flex justify-between items-start mb-2">
                  <div>
                    <span class="font-semibold">
                      {getDiscrepancyTypeDisplay(disc.discrepancyType)}
                    </span>
                    <span class="text-sm text-muted-foreground ml-2">
                      in field: <span class="font-mono">{disc.field}</span>
                    </span>
                  </div>
                  <span
                    class="px-2 py-1 text-xs font-semibold rounded-full bg-warning/10 text-warning"
                  >
                    Major
                  </span>
                </div>
                <p class="text-sm mb-2">{disc.description}</p>
                <div class="grid grid-cols-1 @lg:grid-cols-2 gap-2 mt-2">
                  <div>
                    <p class="text-xs text-muted-foreground">
                      Nightscout Value
                    </p>
                    <p
                      class="font-mono text-sm bg-muted p-2 rounded break-all"
                    >
                      {disc.nightscoutValue || "null"}
                    </p>
                  </div>
                  <div>
                    <p class="text-xs text-muted-foreground">
                      Nocturne Value
                    </p>
                    <p
                      class="font-mono text-sm bg-muted p-2 rounded break-all"
                    >
                      {disc.nocturneValue || "null"}
                    </p>
                  </div>
                </div>
              </div>
            {/each}
          </div>
        </div>
      {/if}

      <!-- Minor Discrepancies -->
      {#if discrepanciesBySeverity.minor.length > 0}
        <div>
          <h3 class="text-lg font-semibold text-info mb-3">
            Minor ({discrepanciesBySeverity.minor.length})
          </h3>
          <div class="space-y-3">
            {#each discrepanciesBySeverity.minor as disc (disc.id)}
              <div
                class="bg-info/10 rounded-lg p-4 border border-info/30"
              >
                <div class="flex justify-between items-start mb-2">
                  <div>
                    <span class="font-semibold">
                      {getDiscrepancyTypeDisplay(disc.discrepancyType)}
                    </span>
                    <span class="text-sm text-muted-foreground ml-2">
                      in field: <span class="font-mono">{disc.field}</span>
                    </span>
                  </div>
                  <span
                    class="px-2 py-1 text-xs font-semibold rounded-full bg-info/10 text-info"
                  >
                    Minor
                  </span>
                </div>
                <p class="text-sm mb-2">{disc.description}</p>
                <div class="grid grid-cols-1 @lg:grid-cols-2 gap-2 mt-2">
                  <div>
                    <p class="text-xs text-muted-foreground">
                      Nightscout Value
                    </p>
                    <p
                      class="font-mono text-sm bg-muted p-2 rounded break-all"
                    >
                      {disc.nightscoutValue || "null"}
                    </p>
                  </div>
                  <div>
                    <p class="text-xs text-muted-foreground">
                      Nocturne Value
                    </p>
                    <p
                      class="font-mono text-sm bg-muted p-2 rounded break-all"
                    >
                      {disc.nocturneValue || "null"}
                    </p>
                  </div>
                </div>
              </div>
            {/each}
          </div>
        </div>
      {/if}
    </div>
  {:else}
    <div class="bg-card rounded-lg shadow p-6">
      <h2 class="text-xl font-semibold mb-4">Discrepancies</h2>
      <p class="text-muted-foreground">
        No discrepancies found. The responses match perfectly!
      </p>
    </div>
  {/if}

  <!-- Error Message -->
  {#if analysis.errorMessage}
    <div
      class="bg-destructive/10 rounded-lg shadow p-6 border border-destructive/30"
    >
      <h2 class="text-xl font-semibold text-destructive mb-4">Error</h2>
      <p class="text-sm font-mono whitespace-pre-wrap">
        {analysis.errorMessage}
      </p>
    </div>
  {/if}
</div>
