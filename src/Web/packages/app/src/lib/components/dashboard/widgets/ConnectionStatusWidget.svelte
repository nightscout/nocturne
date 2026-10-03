<script lang="ts">
  import WidgetCard from "./WidgetCard.svelte";
  import { Badge } from "$lib/components/ui/badge";
  import { Button } from "$lib/components/ui/button";
  import { getRealtimeStore } from "$lib/stores/realtime-store.svelte";

  const realtimeStore = getRealtimeStore();

  // Reactive state from realtime store
  const connection = $derived(realtimeStore.connectionPresentation);
  const isConnected = $derived(connection === "live");
  const connectionError = $derived(
    connection === "unavailable" ? realtimeStore.connectionError : null
  );
  const stats = $derived(realtimeStore.connectionStats);
  const timeSinceUpdate = $derived(realtimeStore.timeSinceUpdate);

  // Connection status styling
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

  // Format time since last update
  const lastUpdateText = $derived.by(() => {
    if (!timeSinceUpdate) return "Never";

    const seconds = Math.floor(timeSinceUpdate / 1000);
    if (seconds < 60) return `${seconds}s ago`;

    const minutes = Math.floor(seconds / 60);
    if (minutes < 60) return `${minutes}m ago`;

    const hours = Math.floor(minutes / 60);
    return `${hours}h ago`;
  });

  // Manual reconnect handler
  function handleReconnect() {
    realtimeStore.reconnect();
  }
</script>

<WidgetCard title="Connection">
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
        onclick={handleReconnect}
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
    </div>
  {/if}

  {#if connectionError}
    <div class="mt-2 p-2 bg-destructive/10 rounded text-xs">
      <p class="font-medium text-destructive">Error:</p>
      <p class="text-muted-foreground">{connectionError.message}</p>
    </div>
  {/if}
</WidgetCard>
