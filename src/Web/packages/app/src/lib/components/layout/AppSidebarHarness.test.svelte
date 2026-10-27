<script lang="ts">
  import * as Sidebar from "#lib/components/ui/sidebar/index.js";
  import { createRealtimeStore } from "#lib/stores/realtime-store.svelte.js";
  import AppSidebar from "./AppSidebar.svelte";
  import type { AuthUser } from "#lib/stores/auth-store.svelte.js";
  import type { TenantSwitcherTarget } from "#lib/utils/tenant-host.js";

  const {
    user = null,
    isGuestSession = false,
    currentSlug = null,
    baseDomain = null,
    tenantTargets = [],
  }: {
    user?: AuthUser | null;
    isGuestSession?: boolean;
    currentSlug?: string | null;
    baseDomain?: string | null;
    tenantTargets?: TenantSwitcherTarget[];
  } = $props();

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
  <AppSidebar {user} {isGuestSession} {currentSlug} {baseDomain} {tenantTargets} />
</Sidebar.Provider>
