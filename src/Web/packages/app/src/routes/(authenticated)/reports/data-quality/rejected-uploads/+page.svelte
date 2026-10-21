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
  import ChevronLeft from "@lucide/svelte/icons/chevron-left";
  import ChevronRight from "@lucide/svelte/icons/chevron-right";
  import Download from "@lucide/svelte/icons/download";
  import FileWarning from "@lucide/svelte/icons/file-warning";
  import Trash2 from "@lucide/svelte/icons/trash-2";
  import type { RejectedUpload } from "$lib/api";

  const pageSize = 25;
  let offset = $state(0);

  const resource = contextResource(
    () => getRejectedUploads({ limit: pageSize, offset }),
    { errorTitle: "Error Loading Rejected Uploads" }
  );

  const rows = $derived<RejectedUpload[]>(resource.current?.data ?? []);
  const total = $derived(resource.current?.pagination?.total ?? 0);
  const hasPrevious = $derived(offset > 0);
  const hasNext = $derived(offset + rows.length < total);

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

  function shortened(row: RejectedUpload): boolean {
    return (row.payloadBytes ?? 0) > (row.payload?.length ?? 0);
  }

  function source(row: RejectedUpload): string {
    return [row.app, row.device, row.origin].filter(Boolean).join(" · ");
  }

  function exportJson() {
    const records = rows.map((row) => ({
      firstSeenAt: row.firstSeenAt,
      lastSeenAt: row.lastSeenAt,
      occurrences: row.occurrences,
      recordType: row.recordType,
      origin: row.origin,
      app: row.app,
      device: row.device,
      reason: row.reason,
      receivedBytes: row.payloadBytes,
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
      await clearRejectedUploads({ ids: rows.map((row) => row.id!) });
      showClearConfirm = false;
      offset = 0;
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
          in them could not be stored as sent. Most uploaders treat them as saved, so they are
          not in your data. Each is listed once, with how many times it was sent. Long text in
          a record is shortened here.
        </p>
      </div>
    </div>
    {#if rows.length > 0}
      <div class="flex shrink-0 gap-2">
        <Button variant="outline" onclick={exportJson}>
          <Download class="mr-2 size-4" aria-hidden="true" />
          Export this page
        </Button>
        <Button variant="outline" onclick={() => (showClearConfirm = true)}>
          <Trash2 class="mr-2 size-4" aria-hidden="true" />
          Clear this page
        </Button>
      </div>
    {/if}
  </div>

  {#if resource.current}
    {#if rows.length === 0}
      <p class="text-sm text-muted-foreground">No rejected uploads.</p>
    {:else}
      <p class="text-sm text-muted-foreground tabular-nums">
        {formatNumber(offset + 1)}–{formatNumber(offset + rows.length)} of {formatNumber(total)}
      </p>
      <Table.Root>
        <Table.Header>
          <Table.Row>
            <Table.Head>Last sent</Table.Head>
            <Table.Head>Times sent</Table.Head>
            <Table.Head>From</Table.Head>
            <Table.Head>Reason</Table.Head>
            <Table.Head>Record</Table.Head>
          </Table.Row>
        </Table.Header>
        <Table.Body>
          {#each rows as row (row.id)}
            <Table.Row class="align-top">
              <Table.Cell class="whitespace-nowrap tabular-nums">
                {formatDateTime(row.lastSeenAt)}
                {#if (row.occurrences ?? 1) > 1}
                  <div class="text-xs text-muted-foreground">
                    First {formatDateTime(row.firstSeenAt)}
                  </div>
                {/if}
              </Table.Cell>
              <Table.Cell class="tabular-nums">{formatNumber(row.occurrences ?? 1)}</Table.Cell>
              <Table.Cell>{source(row)}</Table.Cell>
              <Table.Cell>{row.reason}</Table.Cell>
              <Table.Cell>
                <details>
                  <summary class="cursor-pointer text-sm text-muted-foreground">
                    {row.recordType}
                  </summary>
                  {#if shortened(row)}
                    <p class="mt-2 text-xs text-muted-foreground">
                      Shortened from {formatNumber(row.payloadBytes ?? 0)} bytes.
                    </p>
                  {/if}
                  <pre class="mt-2 max-w-md overflow-x-auto rounded bg-muted p-2 text-xs">{prettyPayload(row.payload)}</pre>
                </details>
              </Table.Cell>
            </Table.Row>
          {/each}
        </Table.Body>
      </Table.Root>
      {#if hasPrevious || hasNext}
        <div class="flex justify-end gap-2">
          <Button
            variant="outline"
            size="sm"
            disabled={!hasPrevious}
            onclick={() => (offset = Math.max(0, offset - pageSize))}
          >
            <ChevronLeft class="mr-1 size-4" aria-hidden="true" />
            Newer
          </Button>
          <Button
            variant="outline"
            size="sm"
            disabled={!hasNext}
            onclick={() => (offset += pageSize)}
          >
            Older
            <ChevronRight class="ml-1 size-4" aria-hidden="true" />
          </Button>
        </div>
      {/if}
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
    Remove the {formatNumber(rows.length)} rejected uploads on this page? Export them first to
    keep a copy. They are not in your data and cannot be recovered once cleared. Uploads not on
    this page are kept.
  {/snippet}
</ConfirmDialog>
