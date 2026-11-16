<script lang="ts">
  import type { ConnectionStream } from "#lib/api/generated/nocturne-api-client.js";
  import { deleteDataSourceData as deleteDataSourceDataRemote } from "#lib/api/generated/services.generated.remote.js";
  import * as Dialog from "#lib/components/ui/dialog/index.js";
  import * as AlertDialog from "#lib/components/ui/alert-dialog/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { Checkbox } from "#lib/components/ui/checkbox/index.js";
  import AlertCircle from "@lucide/svelte/icons/circle-alert";
  import AlertTriangle from "@lucide/svelte/icons/triangle-alert";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import Trash2 from "@lucide/svelte/icons/trash-2";
  import { toast } from "svelte-sonner";
  import { describeSubmitError, errorStatus } from "#lib/forms/index.js";
  import { canManageConnectors } from "#lib/authorization/connector-management.js";
  import { page } from "$app/state";
  import AppLogo from "#lib/components/ui/AppLogo.svelte";
  import { getDataTypeLabel } from "#lib/utils/data-type-labels.js";
  import { formatNumber } from "#lib/utils/formatting.js";
  import ConnectionTime from "./ConnectionTime.svelte";

  interface Props {
    open: boolean;
    /** The stream whose records the dialog deletes, by its `key`. */
    source: ConnectionStream | null;
    /** How many days the stream's record counts cover. */
    windowDays: number | undefined;
    /** Runs after Cancel closes the dialog, so its opener can show what it came from. */
    onCancel?: () => void;
    onDeleteComplete?: () => Promise<void>;
  }

  let {
    open = $bindable(false),
    source,
    windowDays,
    onCancel,
    onDeleteComplete,
  }: Props = $props();

  const canManage = $derived(
    canManageConnectors(
      page.data.effectivePermissions,
      page.data.refusedAsDemoSubject
    )
  );

  // The word the prompt shows is the word the input is compared with, in every language.
  const confirmWord = "DELETE";

  let showDeleteConfirmDialog = $state(false);
  let isDeletingDataSource = $state(false);
  let deleteConfirmText = $state("");
  let allowReimport = $state(false);
  let deleteFailure = $state<{ error?: string; alreadyGone: boolean } | null>(
    null
  );

  const recordCounts = $derived(Object.entries(source?.recordCounts ?? {}));

  function startDelete() {
    deleteFailure = null;
    deleteConfirmText = "";
    allowReimport = false;
    showDeleteConfirmDialog = true;
  }

  function cancel() {
    open = false;
    onCancel?.();
  }

  async function deleteDataSource() {
    if (!source?.key) return;

    isDeletingDataSource = true;
    deleteFailure = null;
    try {
      const result = await deleteDataSourceDataRemote({
        id: source.key,
        allowReimport,
      });
      if (!result.success) {
        deleteFailure = { error: result.error ?? undefined, alreadyGone: false };
        return;
      }

      toast.success(
        result.totalDeleted === 1
          ? `Deleted 1 record from ${source.name}`
          : `Deleted ${formatNumber(result.totalDeleted)} records from ${source.name}`
      );
      showDeleteConfirmDialog = false;
      open = false;
      await onDeleteComplete?.();
    } catch (e) {
      // A rejected remote function throws SvelteKit's `HttpError` — a plain
      // `{ status, body }` object, not an `Error` — so the status is what
      // separates data that was already gone from a delete that failed.
      const alreadyGone = errorStatus(e) === 404;
      deleteFailure = {
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

{#snippet typeCounts()}
  <ul class="space-y-1">
    {#each recordCounts as [type, count] (type)}
      <li class="flex justify-between gap-4">
        <span>{getDataTypeLabel(type)}</span>
        <span class="tabular-nums">{formatNumber(count)}</span>
      </li>
    {/each}
  </ul>
{/snippet}

<Dialog.Root bind:open>
  <Dialog.Content class="max-w-md">
    {#if source}
      <Dialog.Header>
        <Dialog.Title class="flex items-center gap-2">
          <AppLogo icon={source.icon} invertMode class="size-5 object-contain" />
          {source.name}
        </Dialog.Title>
        {#if source.device ?? source.dataSource}
          <Dialog.Description>
            {source.device ?? source.dataSource}
          </Dialog.Description>
        {/if}
      </Dialog.Header>

      <div class="space-y-4 text-sm">
        <div class="grid grid-cols-2 gap-4">
          <div>
            <span class="text-muted-foreground">Last record</span>
            <p class="mt-1 font-medium">
              <ConnectionTime at={source.lastRecordAt} fallback="Never" />
            </p>
          </div>
          <div>
            <span class="text-muted-foreground">Records in the last 24 hours</span>
            <p class="mt-1 font-medium">
              {formatNumber(source.recordsLast24Hours)}
            </p>
          </div>
        </div>

        <div class="space-y-2">
          <p class="text-muted-foreground">
            Records in the last {windowDays} days
          </p>
          {#if recordCounts.length > 0}
            {@render typeCounts()}
          {:else}
            <p class="font-medium">{formatNumber(source.totalRecords)}</p>
          {/if}
        </div>
      </div>

      <Dialog.Footer>
        <Button variant="outline" onclick={cancel}>Cancel</Button>
        {#if canManage}
          <Button variant="outline-destructive" onclick={startDelete}>
            <Trash2 class="h-4 w-4" />
            Delete data...
          </Button>
        {/if}
      </Dialog.Footer>
    {/if}
  </Dialog.Content>
</Dialog.Root>

{#if canManage}
  <AlertDialog.Root bind:open={showDeleteConfirmDialog}>
    <AlertDialog.Content>
      <AlertDialog.Header>
        <AlertDialog.Title
          variant="destructive"
          class="flex items-center gap-2"
        >
          <AlertTriangle class="h-5 w-5" />
          Permanently delete data
        </AlertDialog.Title>
        <AlertDialog.Description class="grid gap-4">
          {#if source}
            <div
              class="rounded-lg border border-destructive/30 bg-destructive/10 p-4 mt-4 text-sm text-destructive"
            >
              <p class="font-semibold">You cannot undo this action.</p>
              <p class="mt-2">
                Nocturne deletes all records from
                <strong>{source.name}</strong>.
              </p>
              {#if recordCounts.length > 0}
                <p class="mt-2">Records in the last {windowDays} days:</p>
                <div class="mt-1">
                  {@render typeCounts()}
                </div>
              {/if}
            </div>

            {#if deleteFailure}
              <div
                class="rounded-lg border border-destructive/30 bg-destructive/10 p-4"
              >
                <div class="flex items-center gap-2 text-destructive">
                  <AlertCircle class="h-5 w-5" />
                  <span class="font-medium">
                    {deleteFailure.alreadyGone
                      ? "Nothing left to delete"
                      : "Failed to delete data"}
                  </span>
                </div>
                <p class="text-sm text-destructive mt-1">
                  {deleteFailure.error}
                </p>
              </div>
            {:else}
              <label class="mt-4 flex items-start gap-3 rounded-lg border p-4 cursor-pointer">
                <Checkbox
                  id="data-source-allow-reimport"
                  class="mt-0.5"
                  bind:checked={allowReimport}
                />
                <span class="space-y-1">
                  <span class="block text-sm font-medium text-foreground">Allow re-import</span>
                  <span class="block text-sm text-muted-foreground">
                    {#if allowReimport}
                      Records this source sends again are imported, including any of its records
                      deleted earlier.
                    {:else}
                      Deleted records are never imported again, even if this source sends them.
                    {/if}
                  </span>
                </span>
              </label>
              <div class="space-y-2 mt-4">
                <label for="confirm-delete" class="text-sm font-medium">
                  Type <strong>{confirmWord}</strong>
                  to confirm:
                </label>
                <Input
                  id="confirm-delete"
                  type="text"
                  bind:value={deleteConfirmText}
                  placeholder={confirmWord}
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
        <Button
          variant="destructive"
          onclick={deleteDataSource}
          disabled={isDeletingDataSource || deleteConfirmText !== confirmWord}
        >
          {#if isDeletingDataSource}
            <Loader2 class="h-4 w-4 animate-spin" />
            Deleting...
          {:else}
            <Trash2 class="h-4 w-4" />
            {allowReimport ? "Delete and allow re-import" : "Delete all data"}
          {/if}
        </Button>
      </AlertDialog.Footer>
    </AlertDialog.Content>
  </AlertDialog.Root>
{/if}
