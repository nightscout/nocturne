<script lang="ts">
  import * as Sidebar from "$lib/components/ui/sidebar";
  import { createRealtimeStore } from "$lib/stores/realtime-store.svelte";
  import AppSidebar from "./AppSidebar.svelte";
  import MobileHeader from "./MobileHeader.svelte";
  import type { AuthUser } from "$lib/stores/auth-store.svelte";
  import { createHelpContext } from "$lib/help/help-context.svelte";
  import HelpPageView from "$lib/components/help/HelpPageView.svelte";
  import { Input } from "$lib/components/ui/input";
  createHelpContext();
  import type { TenantSwitcherTarget } from "$lib/utils/tenant-host";

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
  <Sidebar.Inset>
    <MobileHeader />
    <main>
      <HelpPageView>
        <p>Underlying report</p>
        <Input aria-label="Report filter" />
      </HelpPageView>
    </main>
  </Sidebar.Inset>
</Sidebar.Provider>
