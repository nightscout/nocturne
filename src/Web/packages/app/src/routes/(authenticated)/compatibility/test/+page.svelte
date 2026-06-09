<script lang="ts">
  import { runCompatibilityTest } from "./data.remote";
  import { createPatch } from "diff";

  // UI Components
  import { Button } from "$lib/components/ui/button";
  import * as Card from "$lib/components/ui/card";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { Textarea } from "$lib/components/ui/textarea";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import * as Select from "$lib/components/ui/select";
  import { ArrowLeft, Play, Loader2 } from "lucide-svelte";

  // Form state - default URL matches CompatibilityProxy format
  let nightscoutUrl = $state("https://your-nightscout.herokuapp.com");
  let apiSecret = $state("");
  let queryPath = $state("/api/v1/treatments?count=5");
  let method = $state("GET");
  let requestBody = $state("");

  // Options
  let ignoreNocturneFields = $state(true);
  let hideNullValues = $state(true);
  let showSideBySide = $state(false);
  let hashApiSecret = $state(true); // Hash API secret with SHA1 by default

  /**
   * Hash a string using SHA1 (compatible with Nightscout authentication) Uses
   * SubtleCrypto API which is available in modern browsers
   */
  async function hashApiSecretSha1(secret: string): Promise<string> {
    const encoder = new TextEncoder();
    const data = encoder.encode(secret);
    const hashBuffer = await crypto.subtle.digest("SHA-1", data);
    const hashArray = Array.from(new Uint8Array(hashBuffer));
    return hashArray.map((b) => b.toString(16).padStart(2, "0")).join("");
  }

  // Known Nocturne-specific fields to ignore
  const nocturneOnlyFields = [
    "sourceConnector",
    "sourceType",
    "syncedAt",
    "nocturneId",
    "additionalProperties",
    "data_source",
    "id",
  ];

  // Result state
  let result = $state<Awaited<ReturnType<typeof runCompatibilityTest>> | null>(
    null
  );
  let isLoading = $state(false);
  let error = $state<string | null>(null);

  // Refs for scroll synchronization
  let leftPanelRef = $state<HTMLElement | null>(null);
  let rightPanelRef = $state<HTMLElement | null>(null);
  let isScrolling = false;

  function syncScrollLeft() {
    if (isScrolling || !leftPanelRef || !rightPanelRef) return;
    isScrolling = true;
    rightPanelRef.scrollTop = leftPanelRef.scrollTop;
    rightPanelRef.scrollLeft = leftPanelRef.scrollLeft;
    requestAnimationFrame(() => (isScrolling = false));
  }

  function syncScrollRight() {
    if (isScrolling || !leftPanelRef || !rightPanelRef) return;
    isScrolling = true;
    leftPanelRef.scrollTop = rightPanelRef.scrollTop;
    leftPanelRef.scrollLeft = rightPanelRef.scrollLeft;
    requestAnimationFrame(() => (isScrolling = false));
  }

  // Filtered responses (used for both diff and side-by-side view)
  const filteredResponses = $derived.by(() => {
    if (!result?.nightscoutResponse || !result?.nocturneResponse) {
      return { ns: null, nc: null };
    }

    let nsResponse = result.nightscoutResponse;
    let ncResponse = result.nocturneResponse;

    // Apply filters to both responses
    try {
      let nsJson = JSON.parse(nsResponse);
      let ncJson = JSON.parse(ncResponse);

      // Match Nocturne entries to Nightscout's _id order (for arrays)
      if (Array.isArray(nsJson) && Array.isArray(ncJson)) {
        ncJson = matchEntriesById(ncJson, nsJson);
      }

      // Remove null values from Nocturne that don't exist in Nightscout
      if (hideNullValues) {
        ncJson = stripExtraNulls(ncJson, nsJson);
      }

      // Remove Nocturne-specific fields from Nocturne response
      if (ignoreNocturneFields) {
        ncJson = removeNocturneFields(ncJson);
      }

      // Reorder Nocturne keys to match Nightscout's order
      ncJson = reorderToMatch(ncJson, nsJson);

      nsResponse = JSON.stringify(nsJson, null, 2);
      ncResponse = JSON.stringify(ncJson, null, 2);
    } catch {
      // Not valid JSON, skip filtering
    }

    return { ns: nsResponse, nc: ncResponse };
  });

  // Computed diff
  const diffOutput = $derived.by(() => {
    if (!filteredResponses.ns || !filteredResponses.nc) {
      return null;
    }

    return createPatch(
      "Nightscout Response",
      filteredResponses.ns,
      filteredResponses.nc,
      "Nightscout",
      "Nocturne"
    );
  });

  // Strip null values from Nocturne response only if the field doesn't have null in Nightscout
  function stripExtraNulls(nocturneObj: any, nightscoutObj: any): any {
    if (Array.isArray(nocturneObj)) {
      // If both are arrays, process element by element
      if (Array.isArray(nightscoutObj)) {
        return nocturneObj.map((item, index) =>
          stripExtraNulls(item, nightscoutObj[index])
        );
      }
      return nocturneObj.map((item) => stripExtraNulls(item, undefined));
    }

    if (nocturneObj && typeof nocturneObj === "object") {
      const cleaned: Record<string, any> = {};
      for (const [key, value] of Object.entries(nocturneObj)) {
        const nsValue = nightscoutObj?.[key];

        // If the value is null/undefined, only keep it if Nightscout also has null
        if (value === null || value === undefined) {
          if (nsValue === null) {
            // Nightscout has null, keep it
            cleaned[key] = value;
          }
          // Otherwise, skip this field (don't add it to cleaned)
        } else {
          // Value is not null, recursively process
          cleaned[key] = stripExtraNulls(value, nsValue);
        }
      }
      return cleaned;
    }

    return nocturneObj;
  }

  // Match and reorder Nocturne array entries to align with Nightscout's _id order
  function matchEntriesById(nocturneArr: any[], nightscoutArr: any[]): any[] {
    // Build a map of Nocturne entries by _id
    const ncById = new Map<string, any>();
    const ncWithoutId: any[] = [];

    for (const item of nocturneArr) {
      if (item && typeof item === "object" && item._id) {
        ncById.set(item._id, item);
      } else {
        ncWithoutId.push(item);
      }
    }

    // Reorder Nocturne entries to match Nightscout's _id order
    const matched: any[] = [];
    const usedIds = new Set<string>();

    for (const nsItem of nightscoutArr) {
      if (nsItem && typeof nsItem === "object" && nsItem._id) {
        const ncItem = ncById.get(nsItem._id);
        if (ncItem) {
          matched.push(ncItem);
          usedIds.add(nsItem._id);
        }
      }
    }

    // Add any unmatched Nocturne entries at the end
    for (const [id, item] of ncById) {
      if (!usedIds.has(id)) {
        matched.push(item);
      }
    }

    // Add entries without _id at the end
    matched.push(...ncWithoutId);

    return matched;
  }

  // Remove Nocturne-specific fields recursively
  function removeNocturneFields(obj: any): any {
    if (Array.isArray(obj)) {
      return obj.map(removeNocturneFields);
    }
    if (obj && typeof obj === "object") {
      const cleaned: Record<string, any> = {};
      for (const [key, value] of Object.entries(obj)) {
        if (!nocturneOnlyFields.includes(key)) {
          cleaned[key] = removeNocturneFields(value);
        }
      }
      return cleaned;
    }
    return obj;
  }

  // Reorder Nocturne object keys to match Nightscout's key order
  function reorderToMatch(nocturneObj: any, nightscoutObj: any): any {
    if (Array.isArray(nocturneObj)) {
      if (Array.isArray(nightscoutObj)) {
        // Build a map of Nightscout entries by _id for matching
        const nsById = new Map<string, any>();
        for (const item of nightscoutObj) {
          if (item && typeof item === "object" && item._id) {
            nsById.set(item._id, item);
          }
        }

        // Match each Nocturne entry with its corresponding Nightscout entry by _id
        return nocturneObj.map((ncItem, index) => {
          if (ncItem && typeof ncItem === "object" && ncItem._id) {
            const nsItem = nsById.get(ncItem._id);
            if (nsItem) {
              return reorderToMatch(ncItem, nsItem);
            }
          }
          // Fall back to index-based matching if no _id
          return reorderToMatch(ncItem, nightscoutObj[index]);
        });
      }
      return nocturneObj.map((item) => reorderToMatch(item, undefined));
    }

    if (
      nocturneObj &&
      typeof nocturneObj === "object" &&
      nightscoutObj &&
      typeof nightscoutObj === "object"
    ) {
      const reordered: Record<string, unknown> = {};
      const nocturneRecord = nocturneObj as Record<string, unknown>;
      const nightscoutRecord = nightscoutObj as Record<string, unknown>;
      const nsKeys = Object.keys(nightscoutRecord);
      const ncKeys = Object.keys(nocturneRecord);

      // First, add keys in Nightscout's order
      for (let i = 0; i < nsKeys.length; i++) {
        const key = nsKeys[i];
        if (key in nocturneRecord) {
          reordered[key] = reorderToMatch(nocturneRecord[key], nightscoutRecord[key]);
        }
      }

      // Then, add any remaining Nocturne-only keys
      for (let i = 0; i < ncKeys.length; i++) {
        const key = ncKeys[i];
        if (!(key in reordered)) {
          reordered[key] = nocturneRecord[key];
        }
      }

      return reordered;
    }

    return nocturneObj;
  }

  // Parse diff output for display
  const parsedDiff = $derived.by(() => {
    if (!diffOutput) return [];

    const lines = diffOutput.split("\n");
    return lines.map((line, index) => {
      let type: "header" | "add" | "remove" | "context" | "meta" = "context";
      if (line.startsWith("+++") || line.startsWith("---")) {
        type = "meta";
      } else if (line.startsWith("@@")) {
        type = "header";
      } else if (line.startsWith("+")) {
        type = "add";
      } else if (line.startsWith("-")) {
        type = "remove";
      }
      return { line, type, index };
    });
  });

  // Parse diff for side-by-side view
  const sideBySideDiff = $derived.by(() => {
    if (!diffOutput) return { left: [], right: [] };

    const lines = diffOutput.split("\n");
    const left: { line: string; type: "normal" | "removed" | "empty" }[] = [];
    const right: { line: string; type: "normal" | "added" | "empty" }[] = [];

    for (const line of lines) {
      // Skip meta lines (---, +++, @@)
      if (
        line.startsWith("---") ||
        line.startsWith("+++") ||
        line.startsWith("@@")
      ) {
        continue;
      }

      if (line.startsWith("-")) {
        // Removed line - show on left, empty on right
        left.push({ line: line.slice(1), type: "removed" });
        right.push({ line: "", type: "empty" });
      } else if (line.startsWith("+")) {
        // Added line - empty on left, show on right
        left.push({ line: "", type: "empty" });
        right.push({ line: line.slice(1), type: "added" });
      } else if (line.startsWith(" ") || line === "") {
        // Context line - show on both sides
        const content = line.startsWith(" ") ? line.slice(1) : line;
        left.push({ line: content, type: "normal" });
        right.push({ line: content, type: "normal" });
      }
    }

    return { left, right };
  });

  // Check if responses are identical
  const isIdentical = $derived(
    result?.nightscoutResponse &&
      result?.nocturneResponse &&
      parsedDiff.every((l) => l.type === "context" || l.type === "meta")
  );

  async function runTest() {
    if (!nightscoutUrl || !queryPath) {
      error = "Please enter both Nightscout URL and Query Path";
      return;
    }

    isLoading = true;
    error = null;
    result = null;

    try {
      // Hash the API secret if the option is enabled and a secret is provided
      let secretToSend = apiSecret || undefined;
      if (secretToSend && hashApiSecret) {
        secretToSend = await hashApiSecretSha1(secretToSend);
      }

      result = await runCompatibilityTest({
        nightscoutUrl,
        apiSecret: secretToSend,
        queryPath,
        method,
        requestBody: requestBody || undefined,
      });
    } catch (err) {
      error = err instanceof Error ? err.message : "An error occurred";
    } finally {
      isLoading = false;
    }
  }

  function formatDuration(ms: number | undefined) {
    if (ms === undefined) return "N/A";
    if (ms < 1000) return `${ms}ms`;
    return `${(ms / 1000).toFixed(2)}s`;
  }
</script>

<div class="@container container mx-auto p-6 space-y-6">
  <!-- Header -->
  <div class="flex flex-col gap-3 @lg:flex-row @lg:justify-between @lg:items-center">
    <div>
      <h1 class="text-3xl font-bold">Manual Compatibility Test</h1>
      <p class="text-muted-foreground mt-1">
        Compare API responses between Nightscout and Nocturne
      </p>
    </div>
    <Button variant="outline" href="/compatibility" class="shrink-0">
      <ArrowLeft class="h-4 w-4 mr-2" />
      Back to Dashboard
    </Button>
  </div>

  <!-- Test Form -->
  <Card.Root>
    <Card.Header>
      <Card.Title>Test Configuration</Card.Title>
      <Card.Description>
        Enter the Nightscout server details and API path to test
      </Card.Description>
    </Card.Header>
    <Card.Content class="space-y-4">
      <div class="grid grid-cols-1 @lg:grid-cols-2 gap-4">
        <div class="space-y-2">
          <Label for="nightscoutUrl">Nightscout URL</Label>
          <Input
            id="nightscoutUrl"
            type="url"
            bind:value={nightscoutUrl}
            placeholder="https://your-nightscout.herokuapp.com"
          />
        </div>

        <div class="space-y-2">
          <Label for="apiSecret">
            API Secret
            <span class="text-muted-foreground font-normal ml-1">
              {hashApiSecret ? "(will be hashed)" : "(SHA1 hash or plain)"}
            </span>
          </Label>
          <Input
            id="apiSecret"
            type="password"
            bind:value={apiSecret}
            placeholder="Enter API secret"
          />
          <div class="flex items-center gap-2 pt-1">
            <Checkbox
              id="hashApiSecret"
              checked={hashApiSecret}
              onCheckedChange={(checked: boolean) =>
                (hashApiSecret = checked === true)}
            />
            <Label for="hashApiSecret" class="font-normal text-sm">
              Hash API secret (SHA1)
            </Label>
          </div>
        </div>

        <div class="@lg:col-span-2 space-y-2">
          <Label for="queryPath">Query Path</Label>
          <div class="flex gap-2">
            <Select.Root type="single" bind:value={method}>
              <Select.Trigger class="w-[100px]">
                {method}
              </Select.Trigger>
              <Select.Content>
                <Select.Item value="GET">GET</Select.Item>
                <Select.Item value="POST">POST</Select.Item>
              </Select.Content>
            </Select.Root>
            <Input
              id="queryPath"
              bind:value={queryPath}
              class="flex-1 font-mono"
              placeholder="/api/v1/entries?count=10"
            />
          </div>
        </div>

        {#if method === "POST"}
          <div class="@lg:col-span-2 space-y-2">
            <Label for="requestBody">Request Body (JSON)</Label>
            <Textarea
              id="requestBody"
              bind:value={requestBody}
              class="font-mono h-24"
              placeholder={"key:value"}
            />
          </div>
        {/if}
      </div>

      <!-- Options -->
      <div class="pt-4 border-t space-y-3">
        <div class="flex items-center gap-2">
          <Checkbox
            id="ignoreNocturneFields"
            checked={ignoreNocturneFields}
            onCheckedChange={(checked: boolean) =>
              (ignoreNocturneFields = checked === true)}
          />
          <Label for="ignoreNocturneFields" class="font-normal">
            Ignore Nocturne-specific fields
            <span class="text-muted-foreground ml-1">
              ({nocturneOnlyFields.join(", ")})
            </span>
          </Label>
        </div>
        <div class="flex items-center gap-2">
          <Checkbox
            id="hideNullValues"
            checked={hideNullValues}
            onCheckedChange={(checked: boolean) =>
              (hideNullValues = checked === true)}
          />
          <Label for="hideNullValues" class="font-normal">
            Hide null values
          </Label>
        </div>
        <div class="flex items-center gap-2">
          <Checkbox
            id="showSideBySide"
            checked={showSideBySide}
            onCheckedChange={(checked: boolean) =>
              (showSideBySide = checked === true)}
          />
          <Label for="showSideBySide" class="font-normal">
            Show side-by-side view
          </Label>
        </div>
      </div>
    </Card.Content>
    <Card.Footer>
      <Button onclick={runTest} disabled={isLoading}>
        {#if isLoading}
          <Loader2 class="h-4 w-4 mr-2 animate-spin" />
          Testing...
        {:else}
          <Play class="h-4 w-4 mr-2" />
          Run Test
        {/if}
      </Button>
    </Card.Footer>
  </Card.Root>

  {#if error}
    <Card.Root class="border-destructive">
      <Card.Content class="py-4">
        <p class="text-destructive">{error}</p>
      </Card.Content>
    </Card.Root>
  {/if}

  <!-- Results -->
  {#if result}
    <!-- Status Cards -->
    <div class="grid grid-cols-1 @4xl:grid-cols-4 gap-4">
      <Card.Root>
        <Card.Content class="pt-6">
          <p class="text-sm text-muted-foreground mb-1">Nightscout Status</p>
          <p
            class="text-2xl font-bold {result.nightscoutStatusCode === 200
              ? 'text-green-600'
              : 'text-destructive'}"
          >
            {result.nightscoutStatusCode ?? "Error"}
          </p>
          {#if result.nightscoutError}
            <p class="text-xs text-destructive mt-1">
              {result.nightscoutError}
            </p>
          {/if}
        </Card.Content>
      </Card.Root>

      <Card.Root>
        <Card.Content class="pt-6">
          <p class="text-sm text-muted-foreground mb-1">Nocturne Status</p>
          <p
            class="text-2xl font-bold {result.nocturneStatusCode === 200
              ? 'text-green-600'
              : 'text-destructive'}"
          >
            {result.nocturneStatusCode ?? "Error"}
          </p>
          {#if result.nocturneError}
            <p class="text-xs text-destructive mt-1">{result.nocturneError}</p>
          {/if}
        </Card.Content>
      </Card.Root>

      <Card.Root>
        <Card.Content class="pt-6">
          <p class="text-sm text-muted-foreground mb-1">Nightscout Time</p>
          <p class="text-2xl font-bold">
            {formatDuration(result.nightscoutResponseTimeMs)}
          </p>
        </Card.Content>
      </Card.Root>

      <Card.Root>
        <Card.Content class="pt-6">
          <p class="text-sm text-muted-foreground mb-1">Nocturne Time</p>
          <p class="text-2xl font-bold">
            {formatDuration(result.nocturneResponseTimeMs)}
          </p>
        </Card.Content>
      </Card.Root>
    </div>

    <!-- Match Status -->
    <Card.Root
      class={isIdentical
        ? "border-green-500 bg-green-50 dark:bg-green-900/20"
        : "border-yellow-500 bg-yellow-50 dark:bg-yellow-900/20"}
    >
      <Card.Content class="py-4">
        <p
          class="font-semibold {isIdentical
            ? 'text-green-700 dark:text-green-300'
            : 'text-yellow-700 dark:text-yellow-300'}"
        >
          {isIdentical
            ? "✓ Responses are identical"
            : "⚠ Responses have differences (see diff below)"}
        </p>
      </Card.Content>
    </Card.Root>

    <!-- Diff View -->
    {#if showSideBySide}
      <!-- Side by Side Diff View -->
      <div class="grid grid-cols-1 @lg:grid-cols-2 gap-4">
        <Card.Root>
          <Card.Header class="py-3">
            <Card.Title class="text-base text-red-600">
              Nightscout Response
            </Card.Title>
          </Card.Header>
          <Card.Content class="p-0">
            <div
              bind:this={leftPanelRef}
              onscroll={syncScrollLeft}
              class="overflow-x-auto max-h-[600px] overflow-y-auto"
            >
              <pre
                class="text-xs font-mono leading-tight">{#each sideBySideDiff.left as { line, type }}<span
                    class="block px-3 min-h-[1.25em] {type === 'removed'
                      ? 'bg-red-100 dark:bg-red-900/30 text-red-800 dark:text-red-200'
                      : type === 'empty'
                        ? 'bg-muted/30'
                        : ''}">{line}</span>{/each}</pre>
            </div>
          </Card.Content>
        </Card.Root>
        <Card.Root>
          <Card.Header class="py-3">
            <Card.Title class="text-base text-green-600">
              Nocturne Response
            </Card.Title>
          </Card.Header>
          <Card.Content class="p-0">
            <div
              bind:this={rightPanelRef}
              onscroll={syncScrollRight}
              class="overflow-x-auto max-h-[600px] overflow-y-auto"
            >
              <pre
                class="text-xs font-mono leading-tight">{#each sideBySideDiff.right as { line, type }}<span
                    class="block px-3 min-h-[1.25em] {type === 'added'
                      ? 'bg-green-100 dark:bg-green-900/30 text-green-800 dark:text-green-200'
                      : type === 'empty'
                        ? 'bg-muted/30'
                        : ''}">{line}</span>{/each}</pre>
            </div>
          </Card.Content>
        </Card.Root>
      </div>
    {:else}
      <!-- Unified Diff View -->
      <Card.Root>
        <Card.Header class="py-3 flex-row justify-between items-center">
          <Card.Title class="text-base">Unified Diff</Card.Title>
          <span class="text-sm text-muted-foreground">
            <span class="text-red-600">- Nightscout</span>
            {" / "}
            <span class="text-green-600">+ Nocturne</span>
          </span>
        </Card.Header>
        <Card.Content class="p-0">
          <div class="overflow-x-auto max-h-[600px] overflow-y-auto">
            <pre
              class="text-xs font-mono leading-tight">{#each parsedDiff as { line, type }}<span
                  class="block px-3 {type === 'add'
                    ? 'bg-green-100 dark:bg-green-900/30 text-green-800 dark:text-green-200'
                    : type === 'remove'
                      ? 'bg-red-100 dark:bg-red-900/30 text-red-800 dark:text-red-200'
                      : type === 'header'
                        ? 'bg-blue-100 dark:bg-blue-900/30 text-blue-800 dark:text-blue-200'
                        : type === 'meta'
                          ? 'text-muted-foreground'
                          : ''}">{line}</span>{/each}</pre>
          </div>
        </Card.Content>
      </Card.Root>
    {/if}
  {/if}
</div>
