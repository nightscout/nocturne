<script lang="ts">
  import type { DataSourceDeleteResult } from "#lib/api/generated/nocturne-api-client.js";
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
  import { formatNumber } from "#lib/utils/formatting.js";
  import RecordTypeCounts from "./RecordTypeCounts.svelte";

  interface Props {
    open: boolean;
    /** What the records came from, as the reader knows it. */
    name: string;
    recordCounts: [string, number][];
    /** How many days the record counts cover. */
    windowDays: number | undefined;
    /** What a 404 from the delete means, as a heading and a sentence. */
    notFound: { title: string; message: string };
    onDelete: (allowReimport: boolean) => Promise<DataSourceDeleteResult>;
    onDeleted?: () => void | Promise<void>;
  }

  let {
    open = $bindable(false),
    name,
    recordCounts,
    windowDays,
    notFound,
    onDelete,
    onDeleted,
  }: Props = $props();

  // The word the prompt shows is the word the input is compared with, in every language.
  const confirmWord = "DELETE";

  let isDeleting = $state(false);
  let confirmText = $state("");
  let allowReimport = $state(false);
  let failure = $state<{ title: string; error?: string } | null>(null);

  let wasOpen = false;
  $effect(() => {
    if (open && !wasOpen) {
      failure = null;
      confirmText = "";
      allowReimport = false;
    }
    wasOpen = open;
  });

  async function confirmDelete() {
    isDeleting = true;
    failure = null;
    try {
      const result = await onDelete(allowReimport);
      if (!result.success) {
        failure = { title: "Failed to delete data", error: result.error ?? undefined };
        return;
      }

      toast.success(
        result.totalDeleted === 1
          ? `Deleted 1 record from ${name}`
          : `Deleted ${formatNumber(result.totalDeleted)} records from ${name}`
      );
      open = false;
      await onDeleted?.();
    } catch (e) {
      // A rejected remote function throws SvelteKit's `HttpError`, a plain
      // `{ status, body }` object and not an `Error`, so the status is what
      // separates a delete with nothing to act on from a delete that failed.
      const missing = errorStatus(e) === 404;
      failure = {
        title: missing ? notFound.title : "Failed to delete data",
        error: describeSubmitError(
          e,
          missing ? notFound.message : "Failed to delete data"
        ),
      };
    } finally {
      isDeleting = false;
    }
  }
</script>

<AlertDialog.Root bind:open>
  <AlertDialog.Content>
    <AlertDialog.Header>
      <AlertDialog.Title variant="destructive" class="flex items-center gap-2">
        <AlertTriangle class="h-5 w-5" />
        Permanently delete data
      </AlertDialog.Title>
      <AlertDialog.Description class="grid gap-4">
        <div
          class="rounded-lg border border-destructive/30 bg-destructive/10 p-4 mt-4 text-sm text-destructive"
        >
          <p class="font-semibold">You cannot undo this action.</p>
          <p class="mt-2">
            Nocturne deletes all records from
            <strong>{name}</strong>.
          </p>
          {#if recordCounts.length > 0}
            <p class="mt-2">Records in the last {windowDays} days:</p>
            <div class="mt-1">
              <RecordTypeCounts counts={recordCounts} />
            </div>
          {/if}
        </div>

        {#if failure}
          <div class="rounded-lg border border-destructive/30 bg-destructive/10 p-4">
            <div class="flex items-center gap-2 text-destructive">
              <AlertCircle class="h-5 w-5" />
              <span class="font-medium">{failure.title}</span>
            </div>
            <p class="text-sm text-destructive mt-1">
              {failure.error}
            </p>
          </div>
        {:else}
          <label class="mt-4 flex items-start gap-3 rounded-lg border p-4 cursor-pointer">
            <Checkbox
              id="delete-records-allow-reimport"
              class="mt-0.5"
              bind:checked={allowReimport}
            />
            <span class="space-y-1">
              <span class="block text-sm font-medium text-foreground">Allow re-import</span>
              <span class="block text-sm text-muted-foreground">
                {#if allowReimport}
                  Nocturne imports the records that {name} sends again. This includes
                  the records that you deleted before.
                {:else}
                  Nocturne does not import the deleted records again, even if {name}
                  sends them.
                {/if}
              </span>
            </span>
          </label>
          <div class="space-y-2 mt-4">
            <label for="delete-records-confirm" class="text-sm font-medium">
              Type <strong>{confirmWord}</strong>
              to confirm:
            </label>
            <Input
              id="delete-records-confirm"
              type="text"
              bind:value={confirmText}
              placeholder={confirmWord}
            />
          </div>
        {/if}
      </AlertDialog.Description>
    </AlertDialog.Header>
    <AlertDialog.Footer>
      <AlertDialog.Cancel onclick={() => (open = false)}>Cancel</AlertDialog.Cancel>
      <Button
        variant="destructive"
        onclick={confirmDelete}
        disabled={isDeleting || confirmText !== confirmWord}
      >
        {#if isDeleting}
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
