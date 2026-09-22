<script lang="ts">
  import { goto } from "$app/navigation";
  import { resolve } from "$app/paths";
  import type { GoogleHealthStatus } from "$lib/api/generated/nocturne-api-client";
  import DataSourceRow, { type DataSourceStatus } from "$lib/components/settings/DataSourceRow.svelte";
  import { lastSeen } from "$lib/utils/formatting";
  import { HeartPulse } from "lucide-svelte";

  let { connection }: { connection: GoogleHealthStatus } = $props();
  const status = $derived<DataSourceStatus>(
    !connection.connected ? "offline"
      : connection.errorCode ? "error"
      : connection.previewRequired || !connection.selectedTypes?.length ? "configured"
      : "active"
  );
</script>

<DataSourceRow
  name="Google Health"
  icon={undefined}
  {status}
  statusMessage={connection.errorCode ? "Google Health needs attention. Open the connector for details and recovery options." : undefined}
  lastSyncAttempt={connection.lastAttempt}
  lastSuccessfulSync={connection.lastSync}
  onclick={() => void goto(resolve("/settings/connectors/google-health"))}
>
  {#snippet logo()}<HeartPulse class="h-5 w-5" />{/snippet}
  {#snippet metrics()}
    <p class="text-sm text-muted-foreground">
      {#if !connection.connected}
        Reconnect to resume importing
      {:else if connection.previewRequired}
        Review available data and confirm the import selection
      {:else if !connection.selectedTypes?.length}
        Choose at least one data type to start importing
      {:else if connection.lastSync}
        Last successful sync: {lastSeen(connection.lastSync)}
      {:else}
        Waiting for the first successful sync
      {/if}
    </p>
  {/snippet}
</DataSourceRow>
