<script lang="ts">
  import { Badge } from "#lib/components/ui/badge/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { getRealtimeStore } from "#lib/stores/realtime-store.svelte.js";
  import { relativeAge, time } from "#lib/utils/formatting.js";

  interface Props {
    /**
     * Adds the reconnect count, the error heading "Connection error:" and the
     * error time
     */
    detailed?: boolean;
  }

  let { detailed = false }: Props = $props();

  const realtimeStore = getRealtimeStore();

  const connection = $derived(realtimeStore.connectionPresentation);
  const isConnected = $derived(connection === "live");
  const connectionError = $derived(
    connection === "unavailable" ? realtimeStore.connectionError : null
  );
  const stats = $derived(realtimeStore.connectionStats);

  const statusConfig = $derived.by(() => {
    switch (connection) {
      case "live":
        return {
          variant: "default" as const,
          color: "bg-success",
          text: "Connected",
          description: "Real-time data active",
        };
      case "pending":
        return {
          variant: "secondary" as const,
          color: "bg-warning",
          text: "Connecting...",
          description: "Establishing connection",
        };
      case "denied":
        return {
          variant: "outline" as const,
          color: "bg-gray-500",
          text: "Not available",
          description: "Live updates are not permitted for this view",
        };
      case "unavailable":
        return {
          variant: "destructive" as const,
          color: "bg-destructive",
          text: "Disconnected",
          description: connectionError?.message || "Using cached data",
        };
    }
  });

  const lastUpdateText = $derived(
    realtimeStore.timeSinceUpdate
      ? relativeAge(realtimeStore.lastUpdated, { now: realtimeStore.now })
      : "Never"
  );
</script>

<div class="flex items-center justify-between space-x-2">
  <div class="flex items-center space-x-2">
    <div class="w-2 h-2 rounded-full {statusConfig.color}"></div>
    <Badge variant={statusConfig.variant}>
      {statusConfig.text}
    </Badge>
  </div>

  {#if connection === "unavailable"}
    <Button
      variant="outline"
      size="xs"
      onclick={() => realtimeStore.reconnect()}
    >
      Retry
    </Button>
  {/if}
</div>

<p class="text-xs text-muted-foreground mt-1">
  {statusConfig.description}
</p>

{#if isConnected}
  <div class="mt-2 space-y-1 text-xs text-muted-foreground">
    <div class="flex justify-between">
      <span>Messages:</span>
      <span>{stats.messageCount}</span>
    </div>
    <div class="flex justify-between">
      <span>Last update:</span>
      <span>{lastUpdateText}</span>
    </div>
    {#if detailed && stats.reconnectCount > 0}
      <div class="flex justify-between">
        <span>Reconnects:</span>
        <span>{stats.reconnectCount}</span>
      </div>
    {/if}
  </div>
{/if}

{#if connectionError}
  <div class="mt-2 p-2 bg-destructive/10 rounded text-xs">
    <p class="font-medium text-destructive">
      {detailed ? "Connection error:" : "Error:"}
    </p>
    <p class="text-muted-foreground">{connectionError.message}</p>
    {#if detailed}
      <p class="text-muted-foreground">
        {time(new Date(connectionError.timestamp))}
      </p>
    {/if}
  </div>
{/if}
