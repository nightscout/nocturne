<script lang="ts">
  import { goto } from "$app/navigation";
  import { resolve } from "$app/paths";
  import {
    GoogleHealthConnectionState,
    type GoogleHealthStatus,
  } from "$lib/api/generated/nocturne-api-client";
  import DataSourceRow, {
    type DataSourceStatus,
  } from "$lib/components/settings/DataSourceRow.svelte";
  import { relativeAge } from "$lib/utils/formatting";
  import HeartPulse from "@lucide/svelte/icons/heart-pulse";

  let { connection }: { connection: GoogleHealthStatus } = $props();

  const rowStatus: Record<GoogleHealthConnectionState, DataSourceStatus> = {
    [GoogleHealthConnectionState.Unavailable]: "offline",
    [GoogleHealthConnectionState.Disconnected]: "offline",
    [GoogleHealthConnectionState.ReviewRequired]: "configured",
    [GoogleHealthConnectionState.NothingSelected]: "configured",
    [GoogleHealthConnectionState.Syncing]: "syncing",
    [GoogleHealthConnectionState.Active]: "active",
  };
  const state = $derived(
    connection.connectionState ?? GoogleHealthConnectionState.Disconnected
  );
  const offline = $derived(rowStatus[state] === "offline");
  const failed = $derived(
    !!connection.errorCode && !offline && state !== GoogleHealthConnectionState.Syncing
  );
</script>

<DataSourceRow
  name="Google Health"
  icon={undefined}
  status={failed ? "error" : rowStatus[state]}
  statusMessage={failed
    ? "Google Health needs attention. Open the connector for details and recovery options."
    : undefined}
  lastSyncAttempt={connection.lastAttempt}
  lastSuccessfulSync={connection.lastSync}
  onclick={() => void goto(resolve("/settings/connectors/google-health"))}
>
  {#snippet logo()}<HeartPulse class="h-5 w-5" />{/snippet}
  {#snippet metrics()}
    <p class="text-sm text-muted-foreground">
      {#if state === GoogleHealthConnectionState.Unavailable}
        Not enabled on this Nocturne server
      {:else if state === GoogleHealthConnectionState.Disconnected}
        Reconnect to resume importing
      {:else if state === GoogleHealthConnectionState.ReviewRequired}
        Review available data and confirm the import selection
      {:else if state === GoogleHealthConnectionState.NothingSelected}
        Choose at least one data type to start importing
      {:else if connection.lastSync}
        Last successful sync: {relativeAge(connection.lastSync, { fallback: "Never" })}
      {:else}
        Waiting for the first successful sync
      {/if}
    </p>
  {/snippet}
</DataSourceRow>
