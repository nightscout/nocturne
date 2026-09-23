<script lang="ts">
  import type { DataSourceInfo } from "$lib/api/generated/nocturne-api-client";
  import { deleteDataSourceData as deleteDataSourceDataRemote } from "$api/generated/services.generated.remote";
  import * as Dialog from "$lib/components/ui/dialog";
  import * as AlertDialog from "$lib/components/ui/alert-dialog";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
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
  import { describeSubmitError, errorStatus } from "$lib/forms";
  import { satisfiesScope } from "$lib/authorization/scopes";
  import { page } from "$app/state";
  import { getCategoryIcon } from "$lib/utils/connector-display";
  import { formatNumber, lastSeen } from "$lib/utils/formatting";

  interface Props {
    open: boolean;
    selectedDataSource: DataSourceInfo | null;
    onDeleteComplete?: () => Promise<void>;
  }

  let {
    open = $bindable(false),
    selectedDataSource,
    onDeleteComplete,
  }: Props = $props();

  const canManage = $derived(
    satisfiesScope(page.data.effectivePermissions ?? [], "tenant.settings")
  );

  let showDeleteConfirmDialog = $state(false);
  let isDeletingDataSource = $state(false);
  let deleteConfirmText = $state("");
  let deleteResult = $state<{
    success?: boolean;
    totalDeleted?: number;
    error?: string;
    alreadyGone?: boolean;
  } | null>(null);

  $effect(() => {
    if (open) {
      deleteResult = null;
      deleteConfirmText = "";
    }
  });

  function getStatusBadge(status: string | undefined): {
    variant: "success" | "warning" | "secondary" | "outline";
    text: string;
  } {
    switch (status) {
      case "active":
        return { variant: "success", text: "Active" };
      case "stale":
        return { variant: "warning", text: "Stale" };
      case "inactive":
        return { variant: "outline", text: "Inactive" };
      default:
        return { variant: "secondary", text: "Unknown" };
    }
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
      // A rejected remote function throws SvelteKit's `HttpError` — a plain
      // `{ status, body }` object, not an `Error` — so the status is what
      // separates data that was already gone from a delete that failed.
      const alreadyGone = errorStatus(e) === 404;
      deleteResult = {
        success: false,
        alreadyGone,
        error: describeSubmitError(
          e,
          alreadyGone
            ? "This data source has no data left to delete."
            : "Failed to delete data"
        ),
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
              >
                {getStatusBadge(selectedDataSource.status).text}
              </Badge>
            </div>
          </div>
          <div>
            <span class="text-muted-foreground">Last Record Received</span>
            <p class="mt-1 font-medium">
              {lastSeen(selectedDataSource.lastSeen)}
            </p>
          </div>
          <div>
            <span class="text-muted-foreground">Records (24h)</span>
            <p class="mt-1 font-medium">
              {formatNumber(selectedDataSource.entriesLast24h)}
            </p>
          </div>
          <div>
            <span class="text-muted-foreground">Total Records</span>
            <p class="mt-1 font-medium">
              {formatNumber(selectedDataSource.totalEntries)}
            </p>
          </div>
        </div>

        <Separator />

        {#if canManage}
          <div
            class="rounded-lg border border-warning/30 bg-warning/10 p-4"
          >
            <div class="flex items-start gap-3">
              <AlertTriangle
                class="h-5 w-5 text-warning shrink-0 mt-0.5"
              />
              <div>
                <p
                  class="text-sm font-medium text-warning"
                >
                  Delete All Data from This Source
                </p>
                <p class="text-sm text-warning mt-1">
                  This will permanently delete all entries, treatments, and
                  device status records from this data source.
                </p>
              </div>
            </div>
          </div>
        {/if}
      </div>

      <Dialog.Footer>
        <Button variant="outline" onclick={() => (open = false)}>Cancel</Button>
        {#if canManage}
          <Button
            variant="outline"
            onclick={() => {
              showDeleteConfirmDialog = true;
              deleteConfirmText = "";
            }}
          >
            <Pencil class="h-4 w-4" />
            Delete Data...
          </Button>
        {/if}
      </Dialog.Footer>
    {/if}
  </Dialog.Content>
</Dialog.Root>

<!-- Delete Confirmation Dialog -->
{#if canManage}
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
              class="rounded-lg border border-destructive/30 bg-destructive/10 p-4 mt-4"
            >
              <p class="text-sm font-semibold text-destructive">
                THIS ACTION CANNOT BE UNDONE
              </p>
              <p class="text-sm text-destructive mt-2">
                You are about to permanently delete <strong>all data</strong>
                from
                <strong>{selectedDataSource.name}</strong>
                . This includes:
              </p>
              <ul
                class="text-sm text-destructive list-disc list-inside mt-2 space-y-1"
              >
                <li>
                  All glucose records ({formatNumber(
                    selectedDataSource.totalEntries
                  )} records)
                </li>
                <li>All treatments entered by this device</li>
                <li>All device status records</li>
              </ul>
            </div>

            {#if deleteResult}
              {#if deleteResult.success}
                <div
                  class="rounded-lg border border-success/30 bg-success/10 p-4"
                >
                  <div
                    class="flex items-center gap-2 text-success"
                  >
                    <CheckCircle class="h-5 w-5" />
                    <span class="font-medium">Data deleted successfully</span>
                  </div>
                  <p class="text-sm text-success mt-1">
                    Deleted {formatNumber(deleteResult.totalDeleted)} records
                  </p>
                </div>
              {:else}
                <div
                  class="rounded-lg border border-destructive/30 bg-destructive/10 p-4"
                >
                  <div
                    class="flex items-center gap-2 text-destructive"
                  >
                    <AlertCircle class="h-5 w-5" />
                    <span class="font-medium">
                      {deleteResult.alreadyGone
                        ? "Nothing left to delete"
                        : "Failed to delete data"}
                    </span>
                  </div>
                  <p class="text-sm text-destructive mt-1">
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
                <Input
                  id="confirm-delete"
                  type="text"
                  bind:value={deleteConfirmText}
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
{/if}
