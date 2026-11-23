<script lang="ts">
  import type { ConnectionStream } from "@nocturne/api-client";
  import { deleteDataSourceData as deleteDataSourceDataRemote } from "#lib/api/generated/services.generated.remote.js";
  import * as Dialog from "#lib/components/ui/dialog/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import Trash2 from "@lucide/svelte/icons/trash-2";
  import { canChangeTenantSettings } from "#lib/authorization/tenant-settings.js";
  import { canSetUpConnectors } from "#lib/authorization/connector-setup.js";
  import { page } from "$app/state";
  import AppLogo from "#lib/components/ui/AppLogo.svelte";
  import { formatNumber } from "#lib/utils/formatting.js";
  import ConnectionTime from "./ConnectionTime.svelte";
  import DeleteRecordsConfirmDialog from "./DeleteRecordsConfirmDialog.svelte";
  import RecordTypeCounts from "./RecordTypeCounts.svelte";

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
    canChangeTenantSettings(
      page.data.effectivePermissions,
      page.data.refusedAsDemoSubject
    )
  );

  const canSetUp = $derived(
    canSetUpConnectors(
      page.data.effectivePermissions,
      page.data.refusedAsDemoSubject
    )
  );

  let showDeleteConfirmDialog = $state(false);

  const recordCounts = $derived(Object.entries(source?.recordCounts ?? {}));

  function cancel() {
    open = false;
    onCancel?.();
  }
</script>

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
            <RecordTypeCounts counts={recordCounts} />
          {:else}
            <p class="font-medium">{formatNumber(source.totalRecords)}</p>
          {/if}
        </div>

        {#if canSetUp && !canManage && source.canDelete}
          <p class="text-muted-foreground" data-testid="data-source-delete-reason">
            Only members who can manage this site's settings can delete synced data.
          </p>
        {/if}
      </div>

      <Dialog.Footer>
        <Button variant="outline" onclick={cancel}>Cancel</Button>
        {#if canManage && source.canDelete}
          <Button
            variant="outline-destructive"
            onclick={() => (showDeleteConfirmDialog = true)}
          >
            <Trash2 class="h-4 w-4" />
            Delete data...
          </Button>
        {/if}
      </Dialog.Footer>
    {/if}
  </Dialog.Content>
</Dialog.Root>

{#if canManage && source?.key}
  {@const key = source.key}
  <DeleteRecordsConfirmDialog
    bind:open={showDeleteConfirmDialog}
    name={source.name ?? key}
    {recordCounts}
    {windowDays}
    notFound={{
      title: "Nothing left to delete",
      message: "This data source has no data left to delete.",
    }}
    onDelete={(allowReimport) =>
      deleteDataSourceDataRemote({ id: key, allowReimport })}
    onDeleted={async () => {
      open = false;
      await onDeleteComplete?.();
    }}
  />
{/if}
