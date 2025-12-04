<script lang="ts">
  import { page } from "$app/state";
  import { goto } from "$app/navigation";
  import { getAnalysisDetail } from "../data.remote";

  // Get ID from route params
  const analysisId = $derived(page.params.id);

  // Fetch analysis data using remote function
  const analysisData = $derived(await getAnalysisDetail(analysisId));

  let analysis = $state(analysisData.analysis);

  // Helper to get match type display
  function getMatchTypeDisplay(matchType: number) {
    const types = [
      {
        value: 0,
        label: "Perfect Match",
        class:
          "bg-green-100 text-green-800 dark:bg-green-900 dark:text-green-300",
      },
      {
        value: 1,
        label: "Minor Differences",
        class: "bg-blue-100 text-blue-800 dark:bg-blue-900 dark:text-blue-300",
      },
      {
        value: 2,
        label: "Major Differences",
        class:
          "bg-yellow-100 text-yellow-800 dark:bg-yellow-900 dark:text-yellow-300",
      },
      {
        value: 3,
        label: "Critical Differences",
        class: "bg-red-100 text-red-800 dark:bg-red-900 dark:text-red-300",
      },
      {
        value: 4,
        label: "Nightscout Missing",
        class: "bg-gray-100 text-gray-800 dark:bg-gray-900 dark:text-gray-300",
      },
      {
        value: 5,
        label: "Nocturne Missing",
        class: "bg-gray-100 text-gray-800 dark:bg-gray-900 dark:text-gray-300",
      },
      {
        value: 6,
        label: "Both Missing",
        class: "bg-gray-100 text-gray-800 dark:bg-gray-900 dark:text-gray-300",
      },
      {
        value: 7,
        label: "Comparison Error",
        class: "bg-red-100 text-red-800 dark:bg-red-900 dark:text-red-300",
      },
    ];
    return types.find((t) => t.value === matchType) || types[0];
  }

  // Helper to get discrepancy type display
  function getDiscrepancyTypeDisplay(type: number) {
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

  // Helper to get severity display
  function getSeverityDisplay(severity: number) {
    const severities = [
      {
        value: 0,
        label: "Minor",
        class: "bg-blue-100 text-blue-800 dark:bg-blue-900 dark:text-blue-300",
      },
      {
        value: 1,
        label: "Major",
        class:
          "bg-yellow-100 text-yellow-800 dark:bg-yellow-900 dark:text-yellow-300",
      },
      {
        value: 2,
        label: "Critical",
        class: "bg-red-100 text-red-800 dark:bg-red-900 dark:text-red-300",
      },
    ];
    return severities.find((s) => s.value === severity) || severities[0];
  }

  // Format timestamp
  function formatTime(timestamp: string) {
    return new Date(timestamp).toLocaleString();
  }

  // Format duration
  function formatDuration(ms: number) {
    if (ms < 1000) return `${ms}ms`;
    return `${(ms / 1000).toFixed(2)}s`;
  }

  // Group discrepancies by severity
  let discrepanciesBySeverity = $derived({
    critical:
      analysis.discrepancies?.filter((d: any) => d.severity === 2) || [],
    major: analysis.discrepancies?.filter((d: any) => d.severity === 1) || [],
    minor: analysis.discrepancies?.filter((d: any) => d.severity === 0) || [],
  });

  const matchType = $derived(getMatchTypeDisplay(analysis.overallMatch));
</script>

<div class="container mx-auto p-6 space-y-6">
  <!-- Header with Back Button -->
  <div class="flex items-center gap-4">
    <button
      onclick={() => goto("/compatibility")}
      class="px-4 py-2 bg-gray-600 hover:bg-gray-700 text-white rounded-md transition"
    >
      ← Back
    </button>
    <h1 class="text-3xl font-bold">Request Analysis Detail</h1>
  </div>

  <!-- Overview Card -->
  <div class="bg-white dark:bg-gray-800 rounded-lg shadow p-6">
    <h2 class="text-xl font-semibold mb-4">Overview</h2>
    <div class="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4">
      <div>
        <p class="text-sm text-gray-500 dark:text-gray-400">Correlation ID</p>
        <p class="font-mono text-sm">{analysis.correlationId}</p>
      </div>
      <div>
        <p class="text-sm text-gray-500 dark:text-gray-400">Timestamp</p>
        <p class="text-sm">{formatTime(analysis.analysisTimestamp)}</p>
      </div>
      <div>
        <p class="text-sm text-gray-500 dark:text-gray-400">Overall Match</p>
        <span
          class="inline-block px-3 py-1 text-sm font-semibold rounded-full {matchType.class}"
        >
          {matchType.label}
        </span>
      </div>
      <div>
        <p class="text-sm text-gray-500 dark:text-gray-400">Request Method</p>
        <p class="font-mono font-semibold">{analysis.requestMethod}</p>
      </div>
      <div class="md:col-span-2">
        <p class="text-sm text-gray-500 dark:text-gray-400">Request Path</p>
        <p class="font-mono text-sm break-all">{analysis.requestPath}</p>
      </div>
    </div>
  </div>

  <!-- Status Codes & Response Times -->
  <div class="grid grid-cols-1 md:grid-cols-2 gap-6">
    <!-- Status Codes -->
    <div class="bg-white dark:bg-gray-800 rounded-lg shadow p-6">
      <h2 class="text-xl font-semibold mb-4">Status Codes</h2>
      <div class="space-y-3">
        <div class="flex justify-between items-center">
          <span class="text-gray-600 dark:text-gray-400">Nightscout</span>
          <span
            class="font-mono font-semibold {analysis.statusCodeMatch
              ? 'text-green-600'
              : 'text-red-600'}"
          >
            {analysis.nightscoutStatusCode || "N/A"}
          </span>
        </div>
        <div class="flex justify-between items-center">
          <span class="text-gray-600 dark:text-gray-400">Nocturne</span>
          <span
            class="font-mono font-semibold {analysis.statusCodeMatch
              ? 'text-green-600'
              : 'text-red-600'}"
          >
            {analysis.nocturneStatusCode || "N/A"}
          </span>
        </div>
        <div class="pt-2 border-t dark:border-gray-700">
          <span class="text-gray-600 dark:text-gray-400">Match</span>
          <span
            class="ml-2 {analysis.statusCodeMatch
              ? 'text-green-600'
              : 'text-red-600'} font-semibold"
          >
            {analysis.statusCodeMatch ? "✓ Matched" : "✗ Different"}
          </span>
        </div>
      </div>
    </div>

    <!-- Response Times -->
    <div class="bg-white dark:bg-gray-800 rounded-lg shadow p-6">
      <h2 class="text-xl font-semibold mb-4">Response Times</h2>
      <div class="space-y-3">
        <div class="flex justify-between items-center">
          <span class="text-gray-600 dark:text-gray-400">Nightscout</span>
          <span class="font-mono font-semibold">
            {formatDuration(analysis.nightscoutResponseTimeMs || 0)}
          </span>
        </div>
        <div class="flex justify-between items-center">
          <span class="text-gray-600 dark:text-gray-400">Nocturne</span>
          <span class="font-mono font-semibold">
            {formatDuration(analysis.nocturneResponseTimeMs || 0)}
          </span>
        </div>
        <div class="flex justify-between items-center">
          <span class="text-gray-600 dark:text-gray-400">Total Processing</span>
          <span class="font-mono font-semibold">
            {formatDuration(analysis.totalProcessingTimeMs || 0)}
          </span>
        </div>
        {#if analysis.nightscoutResponseTimeMs && analysis.nocturneResponseTimeMs}
          {@const diff =
            analysis.nocturneResponseTimeMs - analysis.nightscoutResponseTimeMs}
          {@const faster = diff < 0 ? "Nocturne" : "Nightscout"}
          <div class="pt-2 border-t dark:border-gray-700">
            <span class="text-gray-600 dark:text-gray-400">Faster</span>
            <span class="ml-2 font-semibold text-blue-600">
              {faster} by {formatDuration(Math.abs(diff))}
            </span>
          </div>
        {/if}
      </div>
    </div>
  </div>

  <!-- Selection Details -->
  {#if analysis.selectedResponseTarget}
    <div class="bg-white dark:bg-gray-800 rounded-lg shadow p-6">
      <h2 class="text-xl font-semibold mb-4">Response Selection</h2>
      <div class="grid grid-cols-1 md:grid-cols-2 gap-4">
        <div>
          <p class="text-sm text-gray-500 dark:text-gray-400">
            Selected Target
          </p>
          <p class="font-semibold">{analysis.selectedResponseTarget}</p>
        </div>
        <div>
          <p class="text-sm text-gray-500 dark:text-gray-400">Reason</p>
          <p class="text-sm">{analysis.selectionReason || "N/A"}</p>
        </div>
      </div>
    </div>
  {/if}

  <!-- Summary -->
  {#if analysis.summary}
    <div class="bg-white dark:bg-gray-800 rounded-lg shadow p-6">
      <h2 class="text-xl font-semibold mb-4">Summary</h2>
      <p class="text-sm whitespace-pre-wrap">{analysis.summary}</p>
    </div>
  {/if}

  <!-- Discrepancies -->
  {#if analysis.discrepancies && analysis.discrepancies.length > 0}
    <div class="bg-white dark:bg-gray-800 rounded-lg shadow p-6">
      <h2 class="text-xl font-semibold mb-4">
        Discrepancies ({analysis.discrepancies.length})
      </h2>

      <!-- Critical Discrepancies -->
      {#if discrepanciesBySeverity.critical.length > 0}
        <div class="mb-6">
          <h3 class="text-lg font-semibold text-red-600 mb-3">
            Critical ({discrepanciesBySeverity.critical.length})
          </h3>
          <div class="space-y-3">
            {#each discrepanciesBySeverity.critical as disc}
              <div
                class="bg-red-50 dark:bg-red-900/20 rounded-lg p-4 border border-red-200 dark:border-red-800"
              >
                <div class="flex justify-between items-start mb-2">
                  <div>
                    <span class="font-semibold">
                      {getDiscrepancyTypeDisplay(disc.discrepancyType)}
                    </span>
                    <span class="text-sm text-gray-600 dark:text-gray-400 ml-2">
                      in field: <span class="font-mono">{disc.field}</span>
                    </span>
                  </div>
                  <span
                    class="px-2 py-1 text-xs font-semibold rounded-full bg-red-100 text-red-800 dark:bg-red-900 dark:text-red-300"
                  >
                    Critical
                  </span>
                </div>
                <p class="text-sm mb-2">{disc.description}</p>
                <div class="grid grid-cols-1 md:grid-cols-2 gap-2 mt-2">
                  <div>
                    <p class="text-xs text-gray-500 dark:text-gray-400">
                      Nightscout Value
                    </p>
                    <p
                      class="font-mono text-sm bg-gray-100 dark:bg-gray-800 p-2 rounded break-all"
                    >
                      {disc.nightscoutValue || "null"}
                    </p>
                  </div>
                  <div>
                    <p class="text-xs text-gray-500 dark:text-gray-400">
                      Nocturne Value
                    </p>
                    <p
                      class="font-mono text-sm bg-gray-100 dark:bg-gray-800 p-2 rounded break-all"
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
          <h3 class="text-lg font-semibold text-yellow-600 mb-3">
            Major ({discrepanciesBySeverity.major.length})
          </h3>
          <div class="space-y-3">
            {#each discrepanciesBySeverity.major as disc}
              <div
                class="bg-yellow-50 dark:bg-yellow-900/20 rounded-lg p-4 border border-yellow-200 dark:border-yellow-800"
              >
                <div class="flex justify-between items-start mb-2">
                  <div>
                    <span class="font-semibold">
                      {getDiscrepancyTypeDisplay(disc.discrepancyType)}
                    </span>
                    <span class="text-sm text-gray-600 dark:text-gray-400 ml-2">
                      in field: <span class="font-mono">{disc.field}</span>
                    </span>
                  </div>
                  <span
                    class="px-2 py-1 text-xs font-semibold rounded-full bg-yellow-100 text-yellow-800 dark:bg-yellow-900 dark:text-yellow-300"
                  >
                    Major
                  </span>
                </div>
                <p class="text-sm mb-2">{disc.description}</p>
                <div class="grid grid-cols-1 md:grid-cols-2 gap-2 mt-2">
                  <div>
                    <p class="text-xs text-gray-500 dark:text-gray-400">
                      Nightscout Value
                    </p>
                    <p
                      class="font-mono text-sm bg-gray-100 dark:bg-gray-800 p-2 rounded break-all"
                    >
                      {disc.nightscoutValue || "null"}
                    </p>
                  </div>
                  <div>
                    <p class="text-xs text-gray-500 dark:text-gray-400">
                      Nocturne Value
                    </p>
                    <p
                      class="font-mono text-sm bg-gray-100 dark:bg-gray-800 p-2 rounded break-all"
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
          <h3 class="text-lg font-semibold text-blue-600 mb-3">
            Minor ({discrepanciesBySeverity.minor.length})
          </h3>
          <div class="space-y-3">
            {#each discrepanciesBySeverity.minor as disc}
              <div
                class="bg-blue-50 dark:bg-blue-900/20 rounded-lg p-4 border border-blue-200 dark:border-blue-800"
              >
                <div class="flex justify-between items-start mb-2">
                  <div>
                    <span class="font-semibold">
                      {getDiscrepancyTypeDisplay(disc.discrepancyType)}
                    </span>
                    <span class="text-sm text-gray-600 dark:text-gray-400 ml-2">
                      in field: <span class="font-mono">{disc.field}</span>
                    </span>
                  </div>
                  <span
                    class="px-2 py-1 text-xs font-semibold rounded-full bg-blue-100 text-blue-800 dark:bg-blue-900 dark:text-blue-300"
                  >
                    Minor
                  </span>
                </div>
                <p class="text-sm mb-2">{disc.description}</p>
                <div class="grid grid-cols-1 md:grid-cols-2 gap-2 mt-2">
                  <div>
                    <p class="text-xs text-gray-500 dark:text-gray-400">
                      Nightscout Value
                    </p>
                    <p
                      class="font-mono text-sm bg-gray-100 dark:bg-gray-800 p-2 rounded break-all"
                    >
                      {disc.nightscoutValue || "null"}
                    </p>
                  </div>
                  <div>
                    <p class="text-xs text-gray-500 dark:text-gray-400">
                      Nocturne Value
                    </p>
                    <p
                      class="font-mono text-sm bg-gray-100 dark:bg-gray-800 p-2 rounded break-all"
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
    <div class="bg-white dark:bg-gray-800 rounded-lg shadow p-6">
      <h2 class="text-xl font-semibold mb-4">Discrepancies</h2>
      <p class="text-gray-500 dark:text-gray-400">
        No discrepancies found. The responses match perfectly!
      </p>
    </div>
  {/if}

  <!-- Error Message -->
  {#if analysis.errorMessage}
    <div
      class="bg-red-50 dark:bg-red-900/20 rounded-lg shadow p-6 border border-red-200 dark:border-red-800"
    >
      <h2 class="text-xl font-semibold text-red-600 mb-4">Error</h2>
      <p class="text-sm font-mono whitespace-pre-wrap">
        {analysis.errorMessage}
      </p>
    </div>
  {/if}
</div>
