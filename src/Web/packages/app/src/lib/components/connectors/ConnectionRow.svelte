<script module lang="ts">
  /** The DOM id of a connection's row, for a caller that moves focus to it. */
  export function connectionRowId(connectionId: string): string {
    return `connection-${connectionId}`;
  }
</script>

<script lang="ts">
  import {
    ConnectionDirection,
    ConnectionKind,
    ConnectionStatus,
    type Connection,
  } from "#lib/api/generated/nocturne-api-client.js";
  import { Item } from "#lib/components/ui/item/index.js";
  import ChevronRight from "@lucide/svelte/icons/chevron-right";
  import { formatNumber } from "#lib/utils/formatting.js";
  import ConnectionLogo from "./ConnectionLogo.svelte";
  import ConnectionStatusLabel from "./ConnectionStatusLabel.svelte";
  import ConnectionTime from "./ConnectionTime.svelte";

  let {
    connection,
    windowDays,
    syncing = false,
    onclick,
  }: {
    connection: Connection;
    windowDays: number | undefined;
    syncing?: boolean;
    onclick: () => void;
  } = $props();

  const isConnector = $derived(connection.kind === ConnectionKind.Connector);
</script>

<li>
  <Item
    variant="row"
    id={connection.id ? connectionRowId(connection.id) : undefined}
    {onclick}
  >
    <ConnectionLogo {connection} />

    <span class="min-w-0 flex-1 space-y-0.5">
      <span class="flex flex-wrap items-center justify-between gap-x-3 gap-y-1">
        <span class="truncate font-medium">
          {#if connection.name}
            {connection.name}
          {:else}
            API token
          {/if}
        </span>
        <ConnectionStatusLabel status={connection.status} {syncing} />
      </span>

      <span class="block text-sm text-muted-foreground">
        {#if connection.direction === ConnectionDirection.Sends}
          Sends to Nocturne
        {:else if connection.direction === ConnectionDirection.Fetches}
          Nocturne fetches
        {:else}
          No records in the last {windowDays} days
        {/if}
        {#if (connection.totalRecords ?? 0) > 0}
          <span aria-hidden="true">&middot;</span>
          {formatNumber(connection.totalRecords)} records in the last {windowDays} days
        {/if}
      </span>

      <span
        class="flex flex-wrap gap-x-4 gap-y-0.5 text-sm text-muted-foreground"
      >
        {#if connection.lastRecordAt}
          <ConnectionTime label="Last record" at={connection.lastRecordAt} />
        {:else if !isConnector}
          <ConnectionTime
            label="Last used"
            at={connection.lastUsedAt}
            fallback="Never"
          />
        {/if}
        {#if isConnector}
          <ConnectionTime
            label="Last sync"
            at={connection.lastSyncAt}
            fallback="Never"
          />
        {/if}
      </span>

      {#if connection.status === ConnectionStatus.Error && connection.errorMessage}
        <span class="block text-sm text-destructive">
          {connection.errorMessage}
        </span>
      {/if}
    </span>

    <ChevronRight
      class="size-4 shrink-0 text-muted-foreground"
      aria-hidden="true"
    />
  </Item>
</li>
