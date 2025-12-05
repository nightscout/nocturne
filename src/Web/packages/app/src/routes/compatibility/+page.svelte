<script lang="ts">
  import { page } from "$app/state";
  import { goto } from "$app/navigation";
  import { onMount, onDestroy } from "svelte";
  import {
    getCompatibilityData,
    getCompatibilityMetrics,
    getCompatibilityAnalyses,
  } from "./data.remote";
  import type { AnalysisListItemDto } from "$lib/api";

  // Get filter params from URL
  const urlParams = $derived({
    requestPath: page.url.searchParams.get("requestPath") || undefined,
    overallMatch: page.url.searchParams.get("overallMatch")
      ? parseInt(page.url.searchParams.get("overallMatch")!)
      : undefined,
    requestMethod: page.url.searchParams.get("requestMethod") || undefined,
    count: parseInt(page.url.searchParams.get("count") || "100", 10),
    skip: parseInt(page.url.searchParams.get("skip") || "0", 10),
  });

  // Fetch data using remote function
  const fetchedData = $derived(await getCompatibilityData(urlParams));

  // Use derived values for the fetched data to maintain reactivity
  const analyses = $derived(fetchedData.analyses);
  const metrics = $derived(fetchedData.metrics);
  const config = $derived(fetchedData.config);

  // Mutable state
  let isPolling = $state(true);
  let lastUpdate = $state(new Date());
  let nocturneUrl = $state(""); // Auto-detected URL

  // Local override for analyses when polling
  let polledAnalyses = $state<AnalysisListItemDto[] | null>(null);
  let polledMetrics = $state<typeof fetchedData.metrics | null>(null);

  // Polling interval (5 seconds)
  let pollInterval: NodeJS.Timeout | null = null;

  // Filter state - initialized from derived fetchedData
  let filterPath = $state("");
  let filterMethod = $state("");
  let filterMatch = $state("");
  let showCompatible = $state(false); // Hide compatible by default

  // Initialize filter state from fetched data
  $effect(() => {
    filterPath = fetchedData.filters.requestPath || "";
    filterMethod = fetchedData.filters.requestMethod || "";
    filterMatch = fetchedData.filters.overallMatch || "";
  });

  // Helper to get match type display
  function getMatchTypeDisplay(matchType: number | undefined) {
    const types = [
      {
        value: 0,
        label: "Perfect",
        class:
          "bg-green-100 text-green-800 dark:bg-green-900 dark:text-green-300",
      },
      {
        value: 1,
        label: "Minor Diff",
        class: "bg-blue-100 text-blue-800 dark:bg-blue-900 dark:text-blue-300",
      },
      {
        value: 2,
        label: "Major Diff",
        class:
          "bg-yellow-100 text-yellow-800 dark:bg-yellow-900 dark:text-yellow-300",
      },
      {
        value: 3,
        label: "Critical",
        class: "bg-red-100 text-red-800 dark:bg-red-900 dark:text-red-300",
      },
      {
        value: 4,
        label: "NS Missing",
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
        label: "Error",
        class: "bg-red-100 text-red-800 dark:bg-red-900 dark:text-red-300",
      },
    ];
    return types.find((t) => t.value === matchType) || types[0];
  }

  // Helper to determine if analysis is compatible
  function isCompatible(matchType: number | undefined) {
    if (matchType === undefined) return false;
    return matchType === 0 || matchType === 1; // Perfect or Minor Differences
  }

  // Poll for new data
  async function pollData() {
    try {
      const filters = {
        ...(filterPath && { requestPath: filterPath }),
        ...(filterMethod && { requestMethod: filterMethod }),
        ...(filterMatch && { overallMatch: parseInt(filterMatch) }),
        count: 100,
        skip: 0,
      };

      const [metricsResult, analysesResult] = await Promise.all([
        getCompatibilityMetrics(),
        getCompatibilityAnalyses(filters),
      ]);

      polledMetrics = metricsResult;
      polledAnalyses = analysesResult;
      lastUpdate = new Date();
    } catch (err) {
      console.error("Error polling data:", err);
    }
  }

  // Start/stop polling
  onMount(() => {
    // Auto-detect the Nocturne URL from the browser
    if (typeof window !== "undefined") {
      nocturneUrl = `${window.location.protocol}//${window.location.host}`;
    }

    if (isPolling) {
      pollInterval = setInterval(pollData, 5000); // Poll every 5 seconds
    }
  });

  onDestroy(() => {
    if (pollInterval) {
      clearInterval(pollInterval);
    }
  });

  // Toggle polling
  function togglePolling() {
    isPolling = !isPolling;
    if (isPolling) {
      pollInterval = setInterval(pollData, 5000);
    } else if (pollInterval) {
      clearInterval(pollInterval);
      pollInterval = null;
    }
  }

  // Apply filters
  function applyFilters() {
    const params = new URLSearchParams();
    if (filterPath) params.set("requestPath", filterPath);
    if (filterMethod) params.set("requestMethod", filterMethod);
    if (filterMatch) params.set("overallMatch", filterMatch);
    goto(`/compatibility?${params.toString()}`);
  }

  // Clear filters
  function clearFilters() {
    filterPath = "";
    filterMethod = "";
    filterMatch = "";
    goto("/compatibility");
  }

  // Filtered analyses based on showCompatible
  // Use polled data if available, otherwise use the derived fetched data
  const activeAnalyses = $derived(polledAnalyses ?? analyses);
  const activeMetrics = $derived(polledMetrics ?? metrics);

  let filteredAnalyses = $derived(
    showCompatible
      ? activeAnalyses
      : activeAnalyses.filter((a) => !isCompatible(a.overallMatch))
  );

  // Format timestamp
  function formatTime(timestamp: string | Date | undefined) {
    if (!timestamp) return "N/A";
    return new Date(timestamp).toLocaleString();
  }

  // Format duration
  function formatDuration(ms: number) {
    if (ms < 1000) return `${ms}ms`;
    return `${(ms / 1000).toFixed(2)}s`;
  }
</script>

<div class="container mx-auto p-6 space-y-6">
  <!-- Header -->
  <div class="flex justify-between items-center">
    <h1 class="text-3xl font-bold">Compatibility Testing</h1>
    <div class="flex gap-2 items-center">
      <span class="text-sm text-gray-500">
        Last update: {formatTime(lastUpdate.toISOString())}
      </span>
      <button
        onclick={togglePolling}
        class="px-4 py-2 rounded-md {isPolling
          ? 'bg-green-600 hover:bg-green-700'
          : 'bg-gray-600 hover:bg-gray-700'} text-white transition"
      >
        {isPolling ? "Polling Active" : "Polling Paused"}
      </button>
    </div>
  </div>

  <!-- Configuration Card -->
  <div class="bg-white dark:bg-gray-800 rounded-lg shadow p-6">
    <h2 class="text-xl font-semibold mb-4">Configuration</h2>
    <div class="grid grid-cols-1 md:grid-cols-2 gap-4">
      <div>
        <p class="text-sm text-gray-500 dark:text-gray-400">Nightscout URL</p>
        <p class="font-mono text-sm">
          {config.nightscoutUrl || "Not configured"}
        </p>
      </div>
      <div>
        <p class="text-sm text-gray-500 dark:text-gray-400">Nocturne URL</p>
        <p class="font-mono text-sm">{nocturneUrl || "Auto-detecting..."}</p>
      </div>
      <div>
        <p class="text-sm text-gray-500 dark:text-gray-400">Strategy</p>
        <p class="font-mono text-sm">{config.defaultStrategy || "Unknown"}</p>
      </div>
    </div>
  </div>

  <!-- Metrics Cards -->
  <div class="grid grid-cols-1 md:grid-cols-4 gap-4">
    <div class="bg-white dark:bg-gray-800 rounded-lg shadow p-6">
      <h3 class="text-sm text-gray-500 dark:text-gray-400 mb-2">
        Total Requests
      </h3>
      <p class="text-3xl font-bold">{activeMetrics.totalRequests || 0}</p>
    </div>
    <div class="bg-white dark:bg-gray-800 rounded-lg shadow p-6">
      <h3 class="text-sm text-gray-500 dark:text-gray-400 mb-2">
        Compatibility Score
      </h3>
      <p class="text-3xl font-bold">
        {(activeMetrics.compatibilityScore || 0).toFixed(1)}%
      </p>
    </div>
    <div class="bg-white dark:bg-gray-800 rounded-lg shadow p-6">
      <h3 class="text-sm text-gray-500 dark:text-gray-400 mb-2">
        Critical Issues
      </h3>
      <p class="text-3xl font-bold text-red-600">
        {activeMetrics.criticalDifferences || 0}
      </p>
    </div>
    <div class="bg-white dark:bg-gray-800 rounded-lg shadow p-6">
      <h3 class="text-sm text-gray-500 dark:text-gray-400 mb-2">
        Avg Response Time
      </h3>
      <p class="text-3xl font-bold">
        {formatDuration(activeMetrics.averageNocturneResponseTime || 0)}
      </p>
    </div>
  </div>

  <!-- Filters -->
  <div class="bg-white dark:bg-gray-800 rounded-lg shadow p-6">
    <h2 class="text-xl font-semibold mb-4">Filters</h2>
    <div class="grid grid-cols-1 md:grid-cols-4 gap-4">
      <div>
        <label for="filterPath" class="block text-sm font-medium mb-1">
          Request Path
        </label>
        <input
          type="text"
          id="filterPath"
          bind:value={filterPath}
          class="w-full px-3 py-2 border rounded-md dark:bg-gray-700"
          placeholder="/api/v1/entries"
        />
      </div>
      <div>
        <label for="filterMethod" class="block text-sm font-medium mb-1">
          Method
        </label>
        <select
          id="filterMethod"
          bind:value={filterMethod}
          class="w-full px-3 py-2 border rounded-md dark:bg-gray-700"
        >
          <option value="">All</option>
          <option value="GET">GET</option>
          <option value="POST">POST</option>
          <option value="PUT">PUT</option>
          <option value="DELETE">DELETE</option>
        </select>
      </div>
      <div>
        <label for="filterMatch" class="block text-sm font-medium mb-1">
          Match Type
        </label>
        <select
          id="filterMatch"
          bind:value={filterMatch}
          class="w-full px-3 py-2 border rounded-md dark:bg-gray-700"
        >
          <option value="">All</option>
          <option value="0">Perfect</option>
          <option value="1">Minor Differences</option>
          <option value="2">Major Differences</option>
          <option value="3">Critical</option>
        </select>
      </div>
      <div class="flex items-end gap-2">
        <button
          onclick={applyFilters}
          class="px-4 py-2 bg-blue-600 hover:bg-blue-700 text-white rounded-md transition"
        >
          Apply
        </button>
        <button
          onclick={clearFilters}
          class="px-4 py-2 bg-gray-600 hover:bg-gray-700 text-white rounded-md transition"
        >
          Clear
        </button>
      </div>
    </div>
    <div class="mt-4">
      <label class="flex items-center gap-2">
        <input type="checkbox" bind:checked={showCompatible} class="rounded" />
        <span class="text-sm">
          Show compatible requests (Perfect & Minor Differences)
        </span>
      </label>
    </div>
  </div>

  <!-- Analyses Table -->
  <div class="bg-white dark:bg-gray-800 rounded-lg shadow overflow-hidden">
    <div class="px-6 py-4 border-b dark:border-gray-700">
      <h2 class="text-xl font-semibold">
        Recent Requests ({filteredAnalyses.length}{showCompatible
          ? ""
          : " incompatible"})
      </h2>
    </div>
    <div class="overflow-x-auto">
      <table class="w-full">
        <thead class="bg-gray-50 dark:bg-gray-900">
          <tr>
            <th
              class="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase"
            >
              Time
            </th>
            <th
              class="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase"
            >
              Method
            </th>
            <th
              class="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase"
            >
              Path
            </th>
            <th
              class="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase"
            >
              Status
            </th>
            <th
              class="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase"
            >
              Match
            </th>
            <th
              class="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase"
            >
              Issues
            </th>
            <th
              class="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase"
            >
              Response Time
            </th>
          </tr>
        </thead>
        <tbody class="divide-y divide-gray-200 dark:divide-gray-700">
          {#each filteredAnalyses as analysis}
            {@const matchType = getMatchTypeDisplay(analysis.overallMatch)}
            {@const compatible = isCompatible(analysis.overallMatch)}
            <tr
              class="hover:bg-gray-50 dark:hover:bg-gray-900 cursor-pointer transition {compatible
                ? 'opacity-60'
                : ''}"
              onclick={() => goto(`/compatibility/${analysis.id}`)}
            >
              <td class="px-6 py-4 whitespace-nowrap text-sm">
                {formatTime(analysis.analysisTimestamp)}
              </td>
              <td class="px-6 py-4 whitespace-nowrap text-sm font-mono">
                {analysis.requestMethod}
              </td>
              <td class="px-6 py-4 text-sm font-mono truncate max-w-md">
                {analysis.requestPath}
              </td>
              <td class="px-6 py-4 whitespace-nowrap text-sm">
                <span class="font-mono">
                  {analysis.nightscoutStatusCode || "N/A"}
                </span>
                {#if analysis.nightscoutStatusCode !== analysis.nocturneStatusCode}
                  <span class="text-red-600">≠</span>
                  <span class="font-mono">
                    {analysis.nocturneStatusCode || "N/A"}
                  </span>
                {/if}
              </td>
              <td class="px-6 py-4 whitespace-nowrap">
                <span
                  class="px-2 py-1 text-xs font-semibold rounded-full {matchType.class}"
                >
                  {matchType.label}
                </span>
              </td>
              <td class="px-6 py-4 whitespace-nowrap text-sm">
                {#if (analysis.criticalDiscrepancyCount ?? 0) > 0}
                  <span class="text-red-600 font-semibold">
                    {analysis.criticalDiscrepancyCount} critical
                  </span>
                {:else if (analysis.majorDiscrepancyCount ?? 0) > 0}
                  <span class="text-yellow-600 font-semibold">
                    {analysis.majorDiscrepancyCount} major
                  </span>
                {:else if (analysis.minorDiscrepancyCount ?? 0) > 0}
                  <span class="text-blue-600">
                    {analysis.minorDiscrepancyCount} minor
                  </span>
                {:else}
                  <span class="text-green-600">None</span>
                {/if}
              </td>
              <td class="px-6 py-4 whitespace-nowrap text-sm font-mono">
                <div class="flex gap-2">
                  <span title="Nightscout">
                    NS: {formatDuration(analysis.nightscoutResponseTimeMs || 0)}
                  </span>
                  <span title="Nocturne">
                    NC: {formatDuration(analysis.nocturneResponseTimeMs || 0)}
                  </span>
                </div>
              </td>
            </tr>
          {:else}
            <tr>
              <td colspan="7" class="px-6 py-12 text-center text-gray-500">
                No analyses found. Make sure the compatibility proxy service is
                running and receiving traffic.
              </td>
            </tr>
          {/each}
        </tbody>
      </table>
    </div>
  </div>
</div>
