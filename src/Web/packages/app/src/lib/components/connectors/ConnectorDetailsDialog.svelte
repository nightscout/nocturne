<script lang="ts">
  import * as Dialog from "$lib/components/ui/dialog";
  import { Button } from "$lib/components/ui/button";
  import { Badge } from "$lib/components/ui/badge";
  import { Separator } from "$lib/components/ui/separator";
  import * as Tooltip from "$lib/components/ui/tooltip";
  import { getDataTypeLabel } from "$lib/utils/data-type-labels";
  import { getApiClient } from "$lib/api";
  import {
    Cloud,
    Loader2,
    Clock,
    CheckCircle,
    WifiOff,
    AlertCircle,
    Download,
    RefreshCw,
    Database,
    Wrench,
  } from "lucide-svelte";
  import type {
    ConnectorCapabilities,
    SyncRequest,
    SyncResult,
  } from "$lib/api/generated/nocturne-api-client";
  import type { ConnectorStatusWithDescription } from "./ServerConnectorsCard.svelte";

  let {
    open = $bindable(false),
    selectedConnector = null,
    selectedConnectorCapabilities = null,
    onSyncComplete,
  } = $props<{
    open: boolean;
    selectedConnector: ConnectorStatusWithDescription | null;
    selectedConnectorCapabilities: ConnectorCapabilities | null;
    onSyncComplete?: () => Promise<void>;
  }>();

  let granularSyncFrom = $state("");
  let granularSyncTo = $state("");
  let isGranularSyncing = $state(false);
  let granularSyncResult = $state<SyncResult | null>(null);

  let isFoodOnlySyncing = $state(false);
  let foodOnlySyncResult = $state<SyncResult | null>(null);

  $effect(() => {
    if (open) {
      const now = new Date();
      const thirtyDaysAgo = new Date(now.getTime() - 30 * 24 * 60 * 60 * 1000);
      const formatLocal = (d: Date) => {
        const offset = d.getTimezoneOffset() * 60000;
        return new Date(d.getTime() - offset).toISOString().slice(0, 16);
      };
      if (!granularSyncTo) granularSyncTo = formatLocal(now);
      if (!granularSyncFrom) granularSyncFrom = formatLocal(thirtyDaysAgo);
    }
  });


  function formatLastSeen(date?: Date): string {
    if (!date) return "Never";
    const d = new Date(date);
    const diff = Date.now() - d.getTime();
    const minutes = Math.floor(diff / 60000);
    if (minutes < 1) return "Just now";
    if (minutes < 60) return `${minutes}m ago`;
    const hours = Math.floor(minutes / 60);
    if (hours < 24) return `${hours}h ago`;
    return `${Math.floor(hours / 24)}d ago`;
  }

  async function triggerGranularSync() {
    const connectorId = selectedConnector?.id;
    if (!connectorId) return;

    const supportsHistoricalSync =
      selectedConnectorCapabilities?.supportsHistoricalSync ?? true;

    // Fast-feedback UI
    isGranularSyncing = true;
    granularSyncResult = null;

    try {
      const apiClient = getApiClient();
      const request: SyncRequest = supportsHistoricalSync
        ? {
            from: new Date(granularSyncFrom),
            to: new Date(granularSyncTo),
          }
        : {};

      const result = await apiClient.services.triggerConnectorSync(
        connectorId,
        request
      );

      granularSyncResult = result;
      if (onSyncComplete) await onSyncComplete();
    } catch (e) {
      granularSyncResult = {
        success: false,
        message: e instanceof Error ? e.message : "Failed to trigger sync",
        errors: [],
        itemsSynced: {},
      };
      if (onSyncComplete) await onSyncComplete();
    } finally {
      isGranularSyncing = false;
    }
  }

  async function triggerFoodOnlySync() {
    const connectorId = selectedConnector?.id;
    if (!connectorId) return;
    isFoodOnlySyncing = true;
    foodOnlySyncResult = null;

    try {
      const apiClient = getApiClient();
      const request: SyncRequest = {
        from: new Date(granularSyncFrom),
        to: new Date(granularSyncTo),
        dataTypes: ["Food" as any],
      };

      const result = await apiClient.services.triggerConnectorSync(
        connectorId,
        request
      );

      foodOnlySyncResult = result;
      if (result.success && onSyncComplete) {
        await onSyncComplete();
      }
    } catch (e) {
      foodOnlySyncResult = {
        success: false,
        message: e instanceof Error ? e.message : "Failed to sync food data",
        errors: [],
        itemsSynced: {},
      };
    } finally {
      isFoodOnlySyncing = false;
    }
  }
</script>

<Dialog.Root bind:open>
  <Dialog.Content class="max-w-md">
    {#if selectedConnector}
      <Dialog.Header>
        <Dialog.Title class="flex items-center gap-2">
          <Cloud class="h-5 w-5" />
          {selectedConnector.name}
        </Dialog.Title>
        <Dialog.Description>
          Connector health and data metrics
        </Dialog.Description>
      </Dialog.Header>

      <div class="space-y-4 py-4">
        <div class="flex items-center justify-between">
          <span class="text-sm font-medium">Status</span>
          {#if selectedConnector.state === "Syncing"}
            <Badge class="bg-blue-100 text-blue-800 dark:bg-blue-900 dark:text-blue-100">
              <Loader2 class="h-3 w-3 mr-1 animate-spin" />
              Syncing...
            </Badge>
          {:else if selectedConnector.state === "BackingOff"}
            <Badge variant="secondary" class="bg-orange-100 text-orange-800 dark:bg-orange-900 dark:text-orange-100">
              <Clock class="h-3 w-3 mr-1" />
              Backing Off
            </Badge>
          {:else if selectedConnector.isHealthy}
            <Badge class="bg-green-100 text-green-800 dark:bg-green-900 dark:text-green-100">
              <CheckCircle class="h-3 w-3 mr-1" />
              Healthy
            </Badge>
          {:else if selectedConnector.state === "Disabled"}
            <Badge variant="secondary" class="bg-gray-100 text-gray-800 dark:bg-gray-800 dark:text-gray-100">
              <WifiOff class="h-3 w-3 mr-1" />
              Disabled
            </Badge>
          {:else if selectedConnector.status === "Unreachable" || selectedConnector.state === "Offline"}
            <Badge variant="outline">
              <WifiOff class="h-3 w-3 mr-1" />
              Offline
            </Badge>
          {:else}
            <Badge variant="destructive">
              <AlertCircle class="h-3 w-3 mr-1" />
              {selectedConnector.stateMessage ?? selectedConnector.state ?? "Error"}
            </Badge>
          {/if}
        </div>

        {#if selectedConnector.description}
          <div class="text-sm text-muted-foreground">
            {selectedConnector.description}
          </div>
        {/if}

        {#if selectedConnectorCapabilities}
          {#if selectedConnectorCapabilities.supportsHistoricalSync === false}
            <div class="rounded-lg border border-blue-200 dark:border-blue-900 bg-blue-50 dark:bg-blue-950/20 p-3 text-xs text-blue-800 dark:text-blue-200">
              Historical sync is not supported for this connector.
              {#if selectedConnectorCapabilities.maxHistoricalDays}
                Recent data only (last {selectedConnectorCapabilities.maxHistoricalDays} days).
              {/if}
            </div>
          {:else if selectedConnectorCapabilities.maxHistoricalDays}
            <div class="text-xs text-muted-foreground">
              Historical sync limited to the last {selectedConnectorCapabilities.maxHistoricalDays} days.
            </div>
          {/if}
        {/if}

        {#if selectedConnector.state === "Disabled"}
          <div class="rounded-lg border border-gray-200 dark:border-gray-800 bg-gray-50 dark:bg-gray-950/20 p-4">
            <div class="flex items-center gap-2 text-muted-foreground">
              <WifiOff class="h-5 w-5" />
              <span class="font-medium">Connector Disabled</span>
            </div>
            <p class="text-sm text-muted-foreground mt-1">
              This connector is not currently enabled, but historical data from
              previous syncs is still stored. Enable the connector in your
              server configuration to resume syncing, or delete the data below.
            </p>
          </div>
        {:else if selectedConnector.state === "Offline"}
          <div class="rounded-lg border border-gray-200 dark:border-gray-800 bg-gray-50 dark:bg-gray-950/20 p-4">
            <div class="flex items-center gap-2 text-muted-foreground">
              <WifiOff class="h-5 w-5" />
              <span class="font-medium">Connector Not Running</span>
            </div>
            <p class="text-sm text-muted-foreground mt-1">
              This connector is not currently running, but data from previous
              syncs is still stored. You can delete this data below.
            </p>
          </div>
        {/if}

        {#if selectedConnector.status !== "Unreachable" || selectedConnector.totalEntries}
          <Separator />

          <div class="space-y-3">
            <div class="flex items-center justify-between">
              <span class="text-sm text-muted-foreground">Total records</span>
              <Tooltip.Root>
                <Tooltip.Trigger>
                  <span class="font-mono font-medium cursor-help underline decoration-dotted decoration-muted-foreground/50">
                    {selectedConnector.totalEntries?.toLocaleString() ?? 0}
                  </span>
                </Tooltip.Trigger>
                <Tooltip.Portal>
                  <Tooltip.Content class="z-50 overflow-hidden rounded-md bg-popover px-3 py-2 text-sm text-popover-foreground shadow-md">
                    {#if selectedConnector.totalItemsBreakdown && Object.keys(selectedConnector.totalItemsBreakdown).length > 0}
                      <div class="space-y-1">
                        <div class="font-medium text-xs text-muted-foreground mb-1">
                          Breakdown by type:
                        </div>
                        {#each Object.entries(selectedConnector.totalItemsBreakdown) as [type, count]}
                          <div class="flex justify-between gap-4 text-xs">
                            <span>{getDataTypeLabel(type)}</span>
                            <span class="font-mono">
                              {count?.toLocaleString()}
                            </span>
                          </div>
                        {/each}
                      </div>
                    {:else}
                      <span class="text-xs">No breakdown available</span>
                    {/if}
                  </Tooltip.Content>
                </Tooltip.Portal>
              </Tooltip.Root>
            </div>
            <div class="flex items-center justify-between">
              <span class="text-sm text-muted-foreground">
                Records in last 24 hours
              </span>
              <Tooltip.Root>
                <Tooltip.Trigger>
                  <span class="font-mono font-medium cursor-help underline decoration-dotted decoration-muted-foreground/50">
                    {selectedConnector.entriesLast24Hours?.toLocaleString() ?? 0}
                  </span>
                </Tooltip.Trigger>
                <Tooltip.Portal>
                  <Tooltip.Content class="z-50 overflow-hidden rounded-md bg-popover px-3 py-2 text-sm text-popover-foreground shadow-md">
                    {#if selectedConnector.itemsLast24HoursBreakdown && Object.keys(selectedConnector.itemsLast24HoursBreakdown).length > 0}
                      <div class="space-y-1">
                        <div class="font-medium text-xs text-muted-foreground mb-1">
                          Breakdown by type:
                        </div>
                        {#each Object.entries(selectedConnector.itemsLast24HoursBreakdown) as [type, count]}
                          <div class="flex justify-between gap-4 text-xs">
                            <span>{getDataTypeLabel(type)}</span>
                            <span class="font-mono">
                              {count?.toLocaleString()}
                            </span>
                          </div>
                        {/each}
                      </div>
                    {:else}
                      <span class="text-xs">No breakdown available</span>
                    {/if}
                  </Tooltip.Content>
                </Tooltip.Portal>
              </Tooltip.Root>
            </div>
            {#if selectedConnector.lastEntryTime}
              <div class="flex items-center justify-between">
                <span class="text-sm text-muted-foreground">
                  Last record received
                </span>
                <span class="font-medium">
                  {formatLastSeen(selectedConnector.lastEntryTime)}
                </span>
              </div>
            {/if}
            {#if selectedConnector.lastSuccessfulSync}
              <div class="flex items-center justify-between">
                <span class="text-sm text-muted-foreground">
                  Last successful sync
                </span>
                <span class="font-medium">
                  {formatLastSeen(selectedConnector.lastSuccessfulSync)}
                </span>
              </div>
            {/if}
            {#if !selectedConnector.isHealthy && selectedConnector.lastSyncAttempt}
              <div class="flex items-center justify-between">
                <span class="text-sm text-muted-foreground">
                  Last sync attempt
                </span>
                <span class="font-medium text-destructive">
                  {formatLastSeen(selectedConnector.lastSyncAttempt)}
                </span>
              </div>
            {/if}
          </div>
        {:else if selectedConnector.status === "Unreachable"}
          <div class="rounded-lg border border-gray-200 dark:border-gray-800 bg-gray-50 dark:bg-gray-950/20 p-4">
            <div class="flex items-center gap-2 text-muted-foreground">
              <WifiOff class="h-5 w-5" />
              <span class="font-medium">Connector Offline</span>
            </div>
            <p class="text-sm text-muted-foreground mt-1">
              This connector is not currently running or cannot be reached.
              Check your server configuration and logs.
            </p>
          </div>
        {/if}

        {#if (selectedConnector.isHealthy || selectedConnector.state === "Configured") &&
        selectedConnector.state !== "Offline" &&
        (selectedConnectorCapabilities?.supportsManualSync ?? true)}
          <Separator />

          <div class="space-y-3">
            <div class="flex items-center gap-2">
              <Download class="h-4 w-4" />
              <h4 class="font-medium text-sm">Manual Sync</h4>
            </div>
            {#if selectedConnectorCapabilities?.supportsHistoricalSync === false}
              <p class="text-xs text-muted-foreground">
                This connector only supports recent syncs.
              </p>
            {:else}
              <p class="text-xs text-muted-foreground">
                Select a date range to re-sync specific data.
              </p>
              <div class="grid grid-cols-2 gap-2">
                <div class="space-y-1">
                  <label for="granular-sync-from" class="text-xs font-medium">
                    From
                  </label>
                  <input
                    type="datetime-local"
                    id="granular-sync-from"
                    bind:value={granularSyncFrom}
                    class="flex h-9 w-full rounded-md border border-input bg-transparent px-3 py-1 text-sm shadow-sm transition-colors file:border-0 file:bg-transparent file:text-sm file:font-medium placeholder:text-muted-foreground focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50"
                  />
                </div>
                <div class="space-y-1">
                  <label for="granular-sync-to" class="text-xs font-medium">
                    To
                  </label>
                  <input
                    type="datetime-local"
                    id="granular-sync-to"
                    bind:value={granularSyncTo}
                    class="flex h-9 w-full rounded-md border border-input bg-transparent px-3 py-1 text-sm shadow-sm transition-colors file:border-0 file:bg-transparent file:text-sm file:font-medium placeholder:text-muted-foreground focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50"
                  />
                </div>
              </div>
            {/if}

            {#if granularSyncResult}
              <div class="text-xs p-2 rounded {granularSyncResult.success ? 'bg-green-100 text-green-800 dark:bg-green-900/30 dark:text-green-200' : 'bg-red-100 text-red-800 dark:bg-red-900/30 dark:text-red-200'}">
                {#if granularSyncResult.success}
                  <CheckCircle class="inline h-3 w-3 mr-1" />
                  Sync initiated successfully
                  {#if granularSyncResult.itemsSynced}
                    ({Object.values(granularSyncResult.itemsSynced || {}).reduce((a, b) => (a || 0) + (b || 0), 0)} items)
                  {/if}
                {:else}
                  <AlertCircle class="inline h-3 w-3 mr-1" />
                  {granularSyncResult.message || "Sync failed"}
                {/if}
              </div>
            {/if}

            <Button size="sm" variant="outline" class="w-full gap-2" onclick={triggerGranularSync} disabled={isGranularSyncing}>
              {#if isGranularSyncing}
                <Loader2 class="h-3 w-3 animate-spin" />
                Syncing...
              {:else}
                <RefreshCw class="h-3 w-3" />
                Sync Now
              {/if}
            </Button>
          </div>

          {#if selectedConnector?.id === "myfitnesspal"}
            <Separator />

            <div class="space-y-3">
              <div class="flex items-center gap-2">
                <Database class="h-4 w-4" />
                <h4 class="font-medium text-sm">Food Definitions</h4>
              </div>
              <p class="text-xs text-muted-foreground">
                Download food data from MyFitnessPal for the date range above,
                without creating treatments. Useful for populating your food
                database.
              </p>

              {#if foodOnlySyncResult}
                <div class="text-xs p-2 rounded {foodOnlySyncResult.success ? 'bg-green-100 text-green-800 dark:bg-green-900/30 dark:text-green-200' : 'bg-red-100 text-red-800 dark:bg-red-900/30 dark:text-red-200'}">
                  {#if foodOnlySyncResult.success}
                    <CheckCircle class="inline h-3 w-3 mr-1" />
                    Food sync completed
                    {#if foodOnlySyncResult.itemsSynced?.Food}
                      ({foodOnlySyncResult.itemsSynced.Food} foods imported)
                    {/if}
                  {:else}
                    <AlertCircle class="inline h-3 w-3 mr-1" />
                    {foodOnlySyncResult.message || "Food sync failed"}
                  {/if}
                </div>
              {/if}

              <Button size="sm" variant="outline" class="w-full gap-2" onclick={triggerFoodOnlySync} disabled={isFoodOnlySyncing}>
                {#if isFoodOnlySyncing}
                  <Loader2 class="h-3 w-3 animate-spin" />
                  Downloading...
                {:else}
                  <Download class="h-3 w-3" />
                  Download Food Definitions
                {/if}
              </Button>
            </div>
          {/if}
        {/if}
      </div>

      <Dialog.Footer>
        <Button variant="outline" onclick={() => (open = false)}>
          Close
        </Button>
        <Button variant="outline" class="gap-2" href="/settings/connectors/{selectedConnector.id?.toLowerCase()}">
          <Wrench class="h-4 w-4" />
          Configure
        </Button>
      </Dialog.Footer>
    {/if}
  </Dialog.Content>
</Dialog.Root>