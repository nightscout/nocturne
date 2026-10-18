<script lang="ts">
  import { createRealtimeStore } from "$lib/stores/realtime-store.svelte";
  import type { InAppNotificationDto } from "$lib/api/generated/nocturne-api-client";
  import SidebarNotifications from "./SidebarNotifications.svelte";

  let { notifications }: { notifications: InAppNotificationDto[] } = $props();

  // Never initialized, so no socket opens; the notifications are set on it directly.
  const store = createRealtimeStore({
    url: "",
    reconnectAttempts: 0,
    reconnectDelay: 0,
    maxReconnectDelay: 0,
    pingTimeout: 0,
    pingInterval: 0,
  });
  // svelte-ignore state_referenced_locally
  store.inAppNotifications = notifications;
</script>

<SidebarNotifications />
