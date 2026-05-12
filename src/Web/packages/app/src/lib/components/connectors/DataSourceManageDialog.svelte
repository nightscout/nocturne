<script lang="ts">
  import type { DataSourceInfo } from "$lib/api/generated/nocturne-api-client";
  import { deleteDataSourceData as deleteDataSourceDataRemote } from "$api/generated/services.generated.remote";
  import * as Dialog from "$lib/components/ui/dialog";
  import * as AlertDialog from "$lib/components/ui/alert-dialog";
  import { Button } from "$lib/components/ui/button";
  import { Badge } from "$lib/components/ui/badge";
  import { Separator } from "$lib/components/ui/separator";
  import {
    CheckCircle,
    AlertCircle,
    AlertTriangle,
    Loader2,
    Pencil,
    Trash2,
  } from "lucide-svelte";
  import { getCategoryIcon } from "$lib/utils/connector-display";

  let {
    open = $bindable(false),
    selectedDataSource,
    onDeleteComplete,
  } = $props<{
    open: boolean;
    selectedDataSource: DataSourceInfo | null;
    onDeleteComplete?: () => Promise<void>;
  }>();

  let showDeleteConfirmDialog = $state(false);
  let isDeletingDataSource = $state(false);
  let deleteConfirmText = $state("");
  let deleteResult = $state<{
    success?: boolean;
    totalDeleted?: number;
    error?: string;
  } | null>(null);

  $effect(() => {
    if (open) {
      deleteResult = null;
      deleteConfirmText = "";
    }
  });

  function getStatusBadge(status: string | undefined): {
    variant: "default" | "secondary" | "destructive" | "outline";
    text: string;
    class: string;
  } {
    switch (status) {
      case "active":
        return {
          variant: "default" as const,
          text: "Active",
          class: "bg-green-100 text-green-800 dark:bg-green-900 dark:text-green-100",
        };
      case "stale":
        return {
          variant: "secondary" as const,
          text: "Stale",
          class: "bg-yellow-100 text-yellow-800 dark:bg-yellow-900 dark:text-yellow-100",
        };
      case "inactive":
        return {
          variant: "outline" as const,
          text: "Inactive",
          class: "",
        };
      default:
        return {
          variant: "secondary" as const,
          text: "Unknown",
          class: "",
        };
    }
  }

  function formatLastSeen(date?: Date): string {
    if (!date) return "Never";
    const d = new Date(date);
    const diff = Date.now() - d.getTime();
    const minutes = Math.floor(diff / 60000);
    if (minutes < 1) return "Just now";
    if (minutes < 60) return `${minutes}m ago`;
    const hours = Math.floor(minutes / 60);
    if (hours < 24) return `${hours}h ago`;
    const days = Math.floor(hours / 24);
    if (days < 7) return `${days}d ago`;
    return d.toLocaleDateString();
  }

  async function deleteDataSource() {
    if (!selectedDataSource) return;

    isDeletingDataSource = true;
    deleteResult = null;
    try {
      const result = await deleteDataSourceDataRemote(selectedDataSource.id!);
      deleteResult = {
        success: result.success ?? false,
        totalDeleted: result.totalDeleted,
        error: result.error ?? undefined,
      };
      if (result.success) {
        showDeleteConfirmDialog = false;
        open = false;
        if (onDeleteComplete) {
          await onDeleteComplete();
        }
      }
    } catch (e) {
      deleteResult = {
        success: false,
        error: e instanceof Error ? e.message : "Failed to delete data",
      };
    } finally {
      isDeletingDataSource = false;
    }
  }
</script>

<!-- Data Source Management Dialog -->
<Dialog.Root bind:open>
  <Dialog.Content class="max-w-md">
    {#if selectedDataSource}
      {@const Icon = getCategoryIcon(selectedDataSource.category)}
      <Dialog.Header>
        <Dialog.Title class="flex items-center gap-2">
          <Icon class="h-5 w-5" />
          {selectedDataSource.name}
        </Dialog.Title>
        <Dialog.Description>
          {selectedDataSource.description ?? selectedDataSource.deviceId}
        </Dialog.Description>
      </Dialog.Header>

      <div class="space-y-4 py-4">
        <div class="grid grid-cols-2 gap-4 text-sm">
          <div>
            <span class="text-muted-foreground">Status</span>
            <div class="mt-1">
              <Badge
                variant={getStatusBadge(selectedDataSource.status).variant}
                class={getStatusBadge(selectedDataSource.status).class}
              >
                {getStatusBadge(selectedDataSource.status).text}
              </Badge>
            </div>
          </div>
          <div>
            <span class="text-muted-foreground">Last Record Received</span>
            <p class="mt-1 font-medium">
              {formatLastSeen(selectedDataSource.lastSeen)}
            </p>
          </div>
          <div>
            <span class="text-muted-foreground">Records (24h)</span>
            <p class="mt-1 font-medium">
              {selectedDataSource.entriesLast24h?.toLocaleString() ?? 0}
            </p>
          </div>
          <div>
            <span class="text-muted-foreground">Total Records</span>
            <p class="mt-1 font-medium">
              {selectedDataSource.totalEntries?.toLocaleString() ?? 0}
            </p>
          </div>
        </div>

        <Separator />

        <div
          class="rounded-lg border border-amber-200 dark:border-amber-800 bg-amber-50 dark:bg-amber-950/20 p-4"
        >
          <div class="flex items-start gap-3">
            <AlertTriangle
              class="h-5 w-5 text-amber-600 dark:text-amber-400 shrink-0 mt-0.5"
            />
            <div>
              <p class="text-sm font-medium text-amber-800 dark:text-amber-200">
                Delete All Data from This Source
              </p>
              <p class="text-sm text-amber-700 dark:text-amber-300 mt-1">
                This will permanently delete all entries, treatments, and device
                status records from this data source.
              </p>
            </div>
          </div>
        </div>
      </div>

      <Dialog.Footer>
        <Button
          variant="outline"
          onclick={() => (open = false)}
        >
          Cancel
        </Button>
        <Button
          variant="outline"
          class="gap-2"
          onclick={() => { showDeleteConfirmDialog = true; deleteConfirmText = ""; }}
        >
          <Pencil class="h-4 w-4" />
          Delete Data...
        </Button>
      </Dialog.Footer>
    {/if}
  </Dialog.Content>
</Dialog.Root>

<!-- Delete Confirmation Dialog -->
<AlertDialog.Root bind:open={showDeleteConfirmDialog}>
  <AlertDialog.Content>
    <AlertDialog.Header>
      <AlertDialog.Title class="flex items-center gap-2 text-destructive">
        <AlertTriangle class="h-5 w-5" />
        Permanently Delete Data
      </AlertDialog.Title>
      <AlertDialog.Description class="space-y-4">
        {#if selectedDataSource}
          <div
            class="rounded-lg border border-red-200 dark:border-red-800 bg-red-50 dark:bg-red-950/20 p-4 mt-4"
          >
            <p class="text-sm font-semibold text-red-800 dark:text-red-200">
              THIS ACTION CANNOT BE UNDONE
            </p>
            <p class="text-sm text-red-700 dark:text-red-300 mt-2">
              You are about to permanently delete <strong>all data</strong>
              from
              <strong>{selectedDataSource.name}</strong>
              . This includes:
            </p>
            <ul
              class="text-sm text-red-700 dark:text-red-300 list-disc list-inside mt-2 space-y-1"
            >
              <li>
                All glucose records ({selectedDataSource.totalEntries?.toLocaleString() ??
                  0} records)
              </li>
              <li>All treatments entered by this device</li>
              <li>All device status records</li>
            </ul>
          </div>

          {#if deleteResult}
            {#if deleteResult.success}
              <div
                class="rounded-lg border border-green-200 dark:border-green-800 bg-green-50 dark:bg-green-950/20 p-4"
              >
                <div
                  class="flex items-center gap-2 text-green-800 dark:text-green-200"
                >
                  <CheckCircle class="h-5 w-5" />
                  <span class="font-medium">Data deleted successfully</span>
                </div>
                <p class="text-sm text-green-700 dark:text-green-300 mt-1">
                  Deleted {deleteResult.totalDeleted?.toLocaleString() ?? 0} records
                </p>
              </div>
            {:else}
              <div
                class="rounded-lg border border-red-200 dark:border-red-800 bg-red-50 dark:bg-red-950/20 p-4"
              >
                <div
                  class="flex items-center gap-2 text-red-800 dark:text-red-200"
                >
                  <AlertCircle class="h-5 w-5" />
                  <span class="font-medium">Failed to delete data</span>
                </div>
                <p class="text-sm text-red-700 dark:text-red-300 mt-1">
                  {deleteResult.error}
                </p>
              </div>
            {/if}
          {:else}
            <div class="space-y-2 mt-4">
              <label for="confirm-delete" class="text-sm font-medium">
                Type <strong>DELETE</strong>
                to confirm:
              </label>
              <input
                id="confirm-delete"
                type="text"
                bind:value={deleteConfirmText}
                class="w-full px-3 py-2 rounded-md border bg-background text-sm"
                placeholder="Type DELETE"
              />
            </div>
          {/if}
        {/if}
      </AlertDialog.Description>
    </AlertDialog.Header>
    <AlertDialog.Footer>
      <AlertDialog.Cancel onclick={() => (showDeleteConfirmDialog = false)}>
        Cancel
      </AlertDialog.Cancel>
      {#if !deleteResult?.success}
        <Button
          variant="destructive"
          onclick={deleteDataSource}
          disabled={isDeletingDataSource || deleteConfirmText !== "DELETE"}
          class="gap-2"
        >
          {#if isDeletingDataSource}
            <Loader2 class="h-4 w-4 animate-spin" />
            Deleting...
          {:else}
            <Trash2 class="h-4 w-4" />
            Delete All Data
          {/if}
        </Button>
      {/if}
    </AlertDialog.Footer>
  </AlertDialog.Content>
</AlertDialog.Root>
