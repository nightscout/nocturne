<script lang="ts">
  import WidgetCard from "./WidgetCard.svelte";
  import { Badge } from "$lib/components/ui/badge";
  import { Button } from "$lib/components/ui/button";
  import { getRealtimeStore } from "$lib/stores/realtime-store.svelte";

  const realtimeStore = getRealtimeStore();

  // Reactive state from realtime store
  const connectionStatus = $derived(realtimeStore.connectionStatus);
  const isConnected = $derived(realtimeStore.isConnected);
  const connectionError = $derived(realtimeStore.connectionError);
  const stats = $derived(realtimeStore.connectionStats);
  const timeSinceUpdate = $derived(realtimeStore.timeSinceUpdate);

  // Connection status styling
  const statusConfig = $derived.by(() => {
    switch (connectionStatus) {
      case "connected":
        return {
          variant: "default" as const,
          color: "bg-green-500",
          text: "Connected",
          description: "Real-time data active",
        };
      case "connecting":
        return {
          variant: "secondary" as const,
          color: "bg-yellow-500",
          text: "Connecting...",
          description: "Establishing connection",
        };
      case "reconnecting":
        return {
          variant: "secondary" as const,
          color: "bg-orange-500",
          text: "Reconnecting...",
          description: "Attempting to reconnect",
        };
      case "disconnected":
        return {
          variant: "outline" as const,
          color: "bg-gray-500",
          text: "Disconnected",
          description: "Using cached data",
        };
      case "error":
        return {
          variant: "destructive" as const,
          color: "bg-red-500",
          text: "Error",
          description: connectionError?.message || "Connection failed",
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
      <Badge variant={statusConfig.variant} class="text-xs">
        {statusConfig.text}
      </Badge>
    </div>

    {#if !isConnected}
      <Button
        variant="outline"
        size="sm"
        onclick={handleReconnect}
        class="h-6 px-2 text-xs"
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
