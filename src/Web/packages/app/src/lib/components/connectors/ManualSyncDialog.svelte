<script lang="ts">
  import { formatLocale } from "$lib/utils/formatting";
  import * as Dialog from "$lib/components/ui/dialog";
  import { Button } from "$lib/components/ui/button";
  import { Loader2, Download, CheckCircle, AlertCircle } from "lucide-svelte";
  import type { SyncProgressEvent } from "$lib/websocket/types";
  import { formatSyncMessage } from "$lib/utils/sync-messages";
  import { tick } from "svelte";

  export interface BatchSyncResult {
    success: boolean;
    errorMessage?: string;
    totalConnectors: number;
    successfulConnectors: number;
    failedConnectors: number;
    startTime: Date;
    endTime: Date;
    connectorResults: {
      connectorName: string;
      success: boolean;
      errorMessage?: string;
      duration?: string;
    }[];
  }

  interface LogEntry {
    time: string;
    message: string;
  }

  interface Props {
    open: boolean;
    isManualSyncing: boolean;
    manualSyncResult: BatchSyncResult | null;
    syncProgress: SyncProgressEvent | null;
  }

  let {
    open = $bindable(false),
    isManualSyncing = false,
    manualSyncResult = null,
    syncProgress = null,
  }: Props = $props();

  let logEntries = $state<LogEntry[]>([]);
  let logContainer: HTMLDivElement | undefined = $state();

  $effect(() => {
    if (syncProgress?.messageType) {
      const entry: LogEntry = {
        time: new Date().toLocaleTimeString(formatLocale(), { hour: "2-digit", minute: "2-digit", second: "2-digit" }),
        message: formatSyncMessage(syncProgress.messageType, syncProgress.messageParams),
      };
      logEntries = [...logEntries, entry];
      tick().then(() => {
        logContainer?.scrollTo({ top: logContainer.scrollHeight, behavior: "smooth" });
      });
    }
  });

  // Clear log when dialog closes
  $effect(() => {
    if (!open) {
      logEntries = [];
    }
  });

  /**
   * A batch sync can partly succeed, so the outcome comes from the per-connector
   * counts rather than a single boolean. `success: false` with no connectors
   * attempted means the request itself failed.
   */
  const outcome = $derived.by(() => {
    if (!manualSyncResult) return null;
    const { totalConnectors, successfulConnectors, success } = manualSyncResult;
    if (!success && totalConnectors === 0) return "request-failed";
    if (totalConnectors === 0) return "nothing-to-sync";
    if (successfulConnectors === 0) return "all-failed";
    if (successfulConnectors < totalConnectors) return "partial";
    return "all-succeeded";
  });
</script>

<Dialog.Root bind:open>
  <Dialog.Content class="max-w-2xl max-h-[80vh] overflow-y-auto">
    <Dialog.Header>
      <Dialog.Title class="flex items-center gap-2">
        <Download class="h-5 w-5" />
        Manual Sync Results
      </Dialog.Title>
      <Dialog.Description>
        Re-sync data from all enabled connectors for the configured lookback
        period
      </Dialog.Description>
    </Dialog.Header>

    <div class="space-y-4 py-4">
      {#if isManualSyncing}
        <div class="space-y-3">
          <div class="flex items-center gap-2">
            <Loader2 class="h-4 w-4 animate-spin text-primary" />
            <p class="text-sm text-muted-foreground">
              {syncProgress?.connectorName ?? "Connector"} is syncing...
            </p>
          </div>
          {#if logEntries.length > 0}
            <div
              bind:this={logContainer}
              class="max-h-48 overflow-y-auto rounded-md border bg-muted/30 p-3 font-mono text-xs space-y-1"
            >
              {#each logEntries as entry, i (i)}
                <div class="flex gap-2">
                  <span class="text-muted-foreground shrink-0">{entry.time}</span>
                  <span>{entry.message}</span>
                </div>
              {/each}
            </div>
          {/if}
        </div>
      {:else if manualSyncResult}
        {@const elapsedSeconds = Math.round(
          (new Date(manualSyncResult.endTime!).getTime() -
            new Date(manualSyncResult.startTime!).getTime()) /
            1000
        )}
        {@const counts = `${manualSyncResult.successfulConnectors} of ${manualSyncResult.totalConnectors} connectors synced in ${elapsedSeconds}s`}
        {#if outcome === "all-succeeded"}
          <div class="rounded-lg border border-success/30 bg-success/10 p-4">
            <div class="flex items-center gap-2 text-success">
              <CheckCircle class="h-5 w-5" />
              <span class="font-medium">Sync completed</span>
            </div>
            <p class="text-sm text-success mt-1">
              {counts}
            </p>
          </div>
        {:else if outcome === "partial"}
          <div class="rounded-lg border border-warning/30 bg-warning/10 p-4">
            <div class="flex items-center gap-2 text-warning">
              <AlertCircle class="h-5 w-5" />
              <span class="font-medium">
                Sync finished with {manualSyncResult.failedConnectors} failed
              </span>
            </div>
            <p class="text-sm text-warning mt-1">
              {counts}
            </p>
          </div>
        {:else if outcome === "nothing-to-sync"}
          <div class="rounded-lg border bg-muted/30 p-4">
            <div class="flex items-center gap-2">
              <AlertCircle class="h-5 w-5 text-muted-foreground" />
              <span class="font-medium">Nothing to sync</span>
            </div>
            <p class="text-muted-foreground text-sm mt-1">
              No enabled connectors were found.
            </p>
          </div>
        {:else}
          <div class="rounded-lg border border-destructive/30 bg-destructive/10 p-4">
            <div class="flex items-center gap-2 text-destructive">
              <AlertCircle class="h-5 w-5" />
              <span class="font-medium">Sync failed</span>
            </div>
            <p class="text-sm text-destructive mt-1">
              {manualSyncResult.errorMessage ??
                (outcome === "all-failed" ? counts : "The sync could not be started.")}
            </p>
          </div>
        {/if}

        {#if manualSyncResult.connectorResults && manualSyncResult.connectorResults.length > 0}
          <div class="space-y-3">
            <h4 class="font-medium text-sm">Connector Results</h4>
            <div class="space-y-2">
              {#each manualSyncResult.connectorResults as result (result.connectorName)}
                <div class="flex items-center justify-between p-3 rounded-lg border {result.success ? 'border-success/30 bg-success/5' : 'border-destructive/30 bg-destructive/5'}">
                  <div class="flex items-center gap-3">
                    {#if result.success}
                      <CheckCircle class="h-4 w-4 text-success" />
                    {:else}
                      <AlertCircle class="h-4 w-4 text-destructive" />
                    {/if}
                    <div>
                      <p class="font-medium text-sm">{result.connectorName}</p>
                      {#if !result.success && result.errorMessage}
                        <p class="text-xs text-muted-foreground">
                          {result.errorMessage}
                        </p>
                      {/if}
                    </div>
                  </div>
                  <div class="text-right text-xs text-muted-foreground">
                    {#if result.duration}
                      {result.duration}
                    {/if}
                  </div>
                </div>
              {/each}
            </div>
          </div>
        {/if}
      {/if}
    </div>

    <Dialog.Footer>
      <Button variant="outline" onclick={() => (open = false)}>
        Close
      </Button>
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>