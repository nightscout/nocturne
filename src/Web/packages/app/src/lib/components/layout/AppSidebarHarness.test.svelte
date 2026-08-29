<script lang="ts">
  import * as Sidebar from "$lib/components/ui/sidebar";
  import { createRealtimeStore } from "$lib/stores/realtime-store.svelte";
  import AppSidebar from "./AppSidebar.svelte";
  import type { AuthUser } from "$lib/stores/auth-store.svelte";

  const {
    user = null,
    isGuestSession = false,
  }: { user?: AuthUser | null; isGuestSession?: boolean } = $props();

  // The glucose widget reads the store from context. Never initialized, so no socket opens.
  createRealtimeStore({
    url: "",
    reconnectAttempts: 0,
    reconnectDelay: 0,
    maxReconnectDelay: 0,
    pingTimeout: 0,
    pingInterval: 0,
  });
</script>

<Sidebar.Provider>
  <AppSidebar {user} {isGuestSession} />
</Sidebar.Provider>
