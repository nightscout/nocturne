<script lang="ts">
  import { onDestroy } from "svelte";
  import * as Dialog from "$lib/components/ui/dialog";
  import { Button } from "$lib/components/ui/button";
  import { toast } from "svelte-sonner";
  import {
    Link2,
    AlertCircle,
    CheckCircle,
    AlertTriangle,
    Loader2,
  } from "lucide-svelte";
  import {
    startDeduplicationJob,
    getJobStatus as getDeduplicationJobStatus,
    cancelJob as cancelDeduplicationJob,
  } from "$api/generated/deduplications.generated.remote";
  import type { DeduplicationJobStatus } from "$lib/api/generated/nocturne-api-client";

  let { open = $bindable(false), isDeduplicating = $bindable(false) } = $props<{ open: boolean, isDeduplicating?: boolean }>();

  let deduplicationJobId = $state<string | null>(null);
  let deduplicationStatus = $state<DeduplicationJobStatus | null>(null);
  let deduplicationError = $state<string | null>(null);
  let deduplicationStartTime = $state<Date | null>(null);
  let deduplicationPollingInterval = $state<ReturnType<typeof setInterval> | null>(null);

  onDestroy(() => {
    stopDeduplicationPolling();
  });

  async function startDeduplication() {
    isDeduplicating = true;
    deduplicationError = null;
    deduplicationStatus = null;
    deduplicationStartTime = new Date();

    try {
      const result = await startDeduplicationJob();
      if (result.jobId) {
        deduplicationJobId = result.jobId;
        startDeduplicationPolling();
      } else {
        deduplicationError = "Failed to start deduplication job";
        isDeduplicating = false;
        deduplicationStartTime = null;
      }
    } catch (e) {
      deduplicationError = e instanceof Error ? e.message : "Failed to start deduplication";
      isDeduplicating = false;
      deduplicationStartTime = null;
    }
  }

  function startDeduplicationPolling() {
    deduplicationPollingInterval = setInterval(async () => {
      if (!deduplicationJobId) return;

      try {
        const status = await getDeduplicationJobStatus(deduplicationJobId).run();
        if (status) {
          deduplicationStatus = status;

          if (
            status.state === "Completed" ||
            status.state === "Failed" ||
            status.state === "Cancelled"
          ) {
            stopDeduplicationPolling();
            isDeduplicating = false;

            if (status.state === "Failed") {
              deduplicationError = status.result?.errorMessage ?? "Job failed";
            }

            const elapsed = deduplicationStartTime
              ? Date.now() - deduplicationStartTime.getTime()
              : 0;
            const twoMinutes = 2 * 60 * 1000;

            if (!open || elapsed > twoMinutes) {
              if (status.state === "Completed") {
                const processed = status.result?.totalRecordsProcessed?.toLocaleString() ?? "0";
                const groups = status.result?.duplicateGroupsFound?.toLocaleString() ?? "0";
                toast.success("Deduplication complete", {
                  description: `Processed ${processed} records, found ${groups} duplicate groups`,
                });
              } else if (status.state === "Failed") {
                toast.error("Deduplication failed", {
                  description: status.result?.errorMessage ?? "An unknown error occurred",
                });
              } else if (status.state === "Cancelled") {
                toast.info("Deduplication cancelled");
              }
            }

            deduplicationStartTime = null;
          }
        }
      } catch (e) {
        console.error("Failed to get deduplication status:", e);
      }
    }, 2000);
  }

  function stopDeduplicationPolling() {
    if (deduplicationPollingInterval) {
      clearInterval(deduplicationPollingInterval);
      deduplicationPollingInterval = null;
    }
  }

  async function cancelDeduplication() {
    if (!deduplicationJobId) return;

    try {
      const result = await cancelDeduplicationJob(deduplicationJobId);
      if (result.cancelled) {
        stopDeduplicationPolling();
        isDeduplicating = false;
      }
    } catch (e) {
      console.error("Failed to cancel deduplication:", e);
    }
  }

  function closeDeduplicationDialog() {
    open = false;
    if (!isDeduplicating) {
      stopDeduplicationPolling();
      deduplicationJobId = null;
      deduplicationStatus = null;
      deduplicationError = null;
      deduplicationStartTime = null;
    }
  }
</script>

<Dialog.Root bind:open onOpenChange={(o) => !o && closeDeduplicationDialog()}>
  <Dialog.Content class="max-w-md">
    <Dialog.Header>
      <Dialog.Title class="flex items-center gap-2">
        <Link2 class="h-5 w-5 text-primary" />
        Deduplicate Records
      </Dialog.Title>
      <Dialog.Description>
        Link records from multiple data sources that represent the same
        underlying event
      </Dialog.Description>
    </Dialog.Header>

    <div class="space-y-4 py-4">
      {#if deduplicationError}
        <div class="rounded-lg border border-red-200 dark:border-red-800 bg-red-50 dark:bg-red-950/20 p-4">
          <div class="flex items-center gap-2 text-red-800 dark:text-red-200">
            <AlertCircle class="h-5 w-5" />
            <span class="font-medium">Error</span>
          </div>
          <p class="text-sm text-red-700 dark:text-red-300 mt-1">
            {deduplicationError}
          </p>
        </div>
      {:else if deduplicationStatus?.state === "Completed"}
        <div class="rounded-lg border border-green-200 dark:border-green-800 bg-green-50 dark:bg-green-950/20 p-4">
          <div class="flex items-center gap-2 text-green-800 dark:text-green-200">
            <CheckCircle class="h-5 w-5" />
            <span class="font-medium">Deduplication Complete</span>
          </div>
          {#if deduplicationStatus.result}
            <div class="mt-3 space-y-1 text-sm text-green-700 dark:text-green-300">
              <div class="flex justify-between">
                <span>Records processed:</span>
                <span class="font-mono">
                  {deduplicationStatus.result.totalRecordsProcessed?.toLocaleString() ?? 0}
                </span>
              </div>
              <div class="flex justify-between">
                <span>Groups created:</span>
                <span class="font-mono">
                  {deduplicationStatus.result.canonicalGroupsCreated?.toLocaleString() ?? 0}
                </span>
              </div>
              <div class="flex justify-between">
                <span>Duplicates found:</span>
                <span class="font-mono">
                  {deduplicationStatus.result.duplicateGroupsFound?.toLocaleString() ?? 0}
                </span>
              </div>
            </div>
          {/if}
        </div>
      {:else if deduplicationStatus?.state === "Cancelled"}
        <div class="rounded-lg border border-amber-200 dark:border-amber-800 bg-amber-50 dark:bg-amber-950/20 p-4">
          <div class="flex items-center gap-2 text-amber-800 dark:text-amber-200">
            <AlertTriangle class="h-5 w-5" />
            <span class="font-medium">Job Cancelled</span>
          </div>
        </div>
      {:else if isDeduplicating}
        <div class="space-y-4">
          <div class="flex items-center gap-3">
            <Loader2 class="h-5 w-5 animate-spin text-primary" />
            <div>
              <p class="font-medium">Running deduplication...</p>
              <p class="text-sm text-muted-foreground">
                {deduplicationStatus?.progress?.currentPhase ?? "Initializing"}
              </p>
            </div>
          </div>

          {#if deduplicationStatus?.progress}
            <div class="space-y-2">
              <div class="flex justify-between text-sm">
                <span class="text-muted-foreground">Progress</span>
                <span class="font-medium">
                  {deduplicationStatus.progress.percentComplete?.toFixed(1) ?? 0}%
                </span>
              </div>
              <div class="w-full h-2 bg-muted rounded-full overflow-hidden">
                <div
                  class="h-full bg-primary transition-all duration-300"
                  style="width: {deduplicationStatus.progress.percentComplete ?? 0}%"
                ></div>
              </div>
              <div class="grid grid-cols-2 gap-2 text-xs text-muted-foreground">
                <div>
                  Processed: {deduplicationStatus.progress.processedRecords?.toLocaleString() ?? 0}
                  / {deduplicationStatus.progress.totalRecords?.toLocaleString() ?? 0}
                </div>
                <div class="text-right">
                  Groups: {deduplicationStatus.progress.groupsFound?.toLocaleString() ?? 0}
                </div>
              </div>
            </div>
          {/if}

          <p class="text-xs text-muted-foreground">
            You can close this dialog — the job will continue in the background
            and you'll be notified when it's done.
          </p>
        </div>
      {:else}
        <div class="rounded-lg border bg-muted/50 p-4">
          <p class="text-sm text-muted-foreground">
            This process will scan all your glucose records, treatments, and
            state spans to identify and link records that represent the same
            event from different data sources.
          </p>
          <ul class="mt-3 text-sm text-muted-foreground list-disc list-inside space-y-1">
            <li>Records within 30 seconds are considered potential matches</li>
            <li>Matching criteria include timestamps and values</li>
            <li>Original data is preserved; only links are created</li>
            <li>Safe to run multiple times</li>
          </ul>
        </div>
      {/if}
    </div>

    <Dialog.Footer>
      <Button variant="outline" onclick={closeDeduplicationDialog}>
        {deduplicationStatus?.state === "Completed" ? "Done" : "Close"}
      </Button>
      {#if isDeduplicating}
        <Button variant="destructive" onclick={cancelDeduplication} class="gap-2">
          Cancel Job
        </Button>
      {:else if !deduplicationStatus || deduplicationStatus.state === "Failed" || deduplicationStatus.state === "Cancelled"}
        <Button onclick={startDeduplication} class="gap-2">
          <Link2 class="h-4 w-4" />
          Start Deduplication
        </Button>
      {/if}
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>
