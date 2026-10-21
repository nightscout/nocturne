<script lang="ts">
  import { resolve } from "$app/paths";
  import { Button } from "$lib/components/ui/button";
  import { ConfirmDialog } from "$lib/components/ui/confirm-dialog";
  import * as Table from "$lib/components/ui/table";
  import {
    getRejectedUploads,
    clearRejectedUploads,
  } from "$api/generated/rejectedUploads.generated.remote";
  import { contextResource } from "$lib/hooks/resource-context.svelte";
  import { formatDateTime, formatNumber } from "$lib/utils/formatting";
  import ArrowLeft from "@lucide/svelte/icons/arrow-left";
  import Download from "@lucide/svelte/icons/download";
  import FileWarning from "@lucide/svelte/icons/file-warning";
  import Trash2 from "@lucide/svelte/icons/trash-2";
  import type { RejectedUpload } from "$lib/api";

  const resource = contextResource(() => getRejectedUploads({ limit: 1000 }), {
    errorTitle: "Error Loading Rejected Uploads",
  });

  const rows = $derived<RejectedUpload[]>(resource.current?.data ?? []);
  const total = $derived(resource.current?.pagination?.total ?? 0);

  let showClearConfirm = $state(false);
  let clearing = $state(false);

  function parsed(payload: string | undefined): unknown {
    if (!payload) return null;
    try {
      return JSON.parse(payload);
    } catch {
      return payload;
    }
  }

  function prettyPayload(payload: string | undefined): string {
    const value = parsed(payload);
    return typeof value === "string" ? value : JSON.stringify(value, null, 2);
  }

  function source(row: RejectedUpload): string {
    return [row.app, row.device, row.origin].filter(Boolean).join(" · ");
  }

  function exportJson() {
    const records = rows.map((row) => ({
      receivedAt: row.receivedAt,
      recordType: row.recordType,
      origin: row.origin,
      app: row.app,
      device: row.device,
      reason: row.reason,
      record: parsed(row.payload),
    }));
    const blob = new Blob([JSON.stringify(records, null, 2)], {
      type: "application/json",
    });
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = `nocturne-rejected-uploads-${new Date().toISOString().split("T")[0]}.json`;
    a.click();
    URL.revokeObjectURL(url);
  }

  async function handleClear() {
    clearing = true;
    try {
      await clearRejectedUploads();
      showClearConfirm = false;
    } finally {
      clearing = false;
    }
  }
</script>

<svelte:head>
  <title>Rejected Uploads - Nocturne</title>
</svelte:head>

<div class="@container space-y-6">
  <a
    href={resolve("/reports/data-quality")}
    class="inline-flex items-center gap-1 text-sm text-muted-foreground hover:text-foreground"
  >
    <ArrowLeft class="size-4" aria-hidden="true" />
    Data Quality
  </a>

  <div class="flex flex-col gap-4 @lg:flex-row @lg:items-start @lg:justify-between">
    <div class="flex items-start gap-3">
      <div class="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg bg-primary/10">
        <FileWarning class="h-5 w-5 text-primary" />
      </div>
      <div class="space-y-1">
        <h1 class="text-xl font-semibold">Rejected Uploads</h1>
        <p class="text-sm text-muted-foreground">
          Records an uploader sent that Nocturne answered but could not store, because a value
          in them could not be stored as sent. The uploader treats them as saved and does not
          send them again, so they are kept here as received.
        </p>
      </div>
    </div>
    {#if rows.length > 0}
      <div class="flex shrink-0 gap-2">
        <Button variant="outline" onclick={exportJson}>
          <Download class="mr-2 size-4" aria-hidden="true" />
          Export JSON
        </Button>
        <Button variant="outline" onclick={() => (showClearConfirm = true)}>
          <Trash2 class="mr-2 size-4" aria-hidden="true" />
          Clear
        </Button>
      </div>
    {/if}
  </div>

  {#if resource.current}
    {#if rows.length === 0}
      <p class="text-sm text-muted-foreground">No rejected uploads.</p>
    {:else}
      <p class="text-sm text-muted-foreground tabular-nums">
        {#if total === 1}
          1 rejected upload.
        {:else if total > rows.length}
          {formatNumber(total)} rejected uploads; the newest {formatNumber(rows.length)} are shown.
        {:else}
          {formatNumber(total)} rejected uploads.
        {/if}
      </p>
      <Table.Root>
        <Table.Header>
          <Table.Row>
            <Table.Head>Received</Table.Head>
            <Table.Head>From</Table.Head>
            <Table.Head>Reason</Table.Head>
            <Table.Head>Record</Table.Head>
          </Table.Row>
        </Table.Header>
        <Table.Body>
          {#each rows as row (row.id)}
            <Table.Row class="align-top">
              <Table.Cell class="whitespace-nowrap tabular-nums">
                {formatDateTime(row.receivedAt)}
              </Table.Cell>
              <Table.Cell>{source(row)}</Table.Cell>
              <Table.Cell>{row.reason}</Table.Cell>
              <Table.Cell>
                <details>
                  <summary class="cursor-pointer text-sm text-muted-foreground">
                    {row.recordType}
                  </summary>
                  <pre class="mt-2 max-w-md overflow-x-auto rounded bg-muted p-2 text-xs">{prettyPayload(row.payload)}</pre>
                </details>
              </Table.Cell>
            </Table.Row>
          {/each}
        </Table.Body>
      </Table.Root>
    {/if}
  {/if}
</div>

<ConfirmDialog
  bind:open={showClearConfirm}
  title="Clear rejected uploads"
  confirmLabel={clearing ? "Clearing..." : "Clear"}
  destructive
  busy={clearing}
  onConfirm={handleClear}
>
  {#snippet description()}
    Remove all {formatNumber(total)} rejected uploads? Export them first to keep a copy. The
    records are not in your data and cannot be recovered once cleared.
  {/snippet}
</ConfirmDialog>
