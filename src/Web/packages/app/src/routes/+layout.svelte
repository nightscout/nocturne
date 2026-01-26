<script lang="ts">
  import "../app.css";
  import { createRealtimeStore } from "$lib/stores/realtime-store.svelte";
  import { createSettingsStore } from "$lib/stores/settings-store.svelte";
  import { createAuthStore } from "$lib/stores/auth-store.svelte";
  import { onMount, onDestroy } from "svelte";
  import * as Sidebar from "$lib/components/ui/sidebar";
  import { AppSidebar, MobileHeader } from "$lib/components/layout";
  import type { LayoutData } from "./$types";
  import { getTitleFaviconService } from "$lib/services/title-favicon-service.svelte";
  import { getDefaultSettings } from "$lib/components/settings/constants";
  import type { AlarmVisualSettings } from "$lib/types/alarm-profile";
  import type { TitleFaviconSettings } from "$lib/stores/serverSettings";
  import { browser } from "$app/environment";
  import { ModeWatcher } from "mode-watcher";
  import * as Card from "$lib/components/ui/card";

  // LocalStorage key for title/favicon settings
  const SETTINGS_STORAGE_KEY = "nocturne-title-favicon-settings";

  // WebSocket config - defaults, can be overridden in production
  const config = {
    url: typeof window !== "undefined" ? window.location.origin : "",
    reconnectAttempts: 10,
    reconnectDelay: 5000,
    maxReconnectDelay: 30000,
    pingTimeout: 60000,
    pingInterval: 25000,
  };

  const { data, children } = $props<{ data: LayoutData; children: any }>();

  const realtimeStore = createRealtimeStore(config);
  createAuthStore(); // Initialize auth store in context

  // Create settings store in context for the entire app
  // This makes feature settings available on all pages including the main dashboard
  createSettingsStore();

  // Title/Favicon service for dynamic updates
  const titleFaviconService = getTitleFaviconService();
  const defaultSettings = getDefaultSettings();

  // Load settings from localStorage with defaults
  function loadTitleFaviconSettings(): TitleFaviconSettings {
    if (!browser) return defaultSettings.titleFavicon;
    try {
      const stored = localStorage.getItem(SETTINGS_STORAGE_KEY);
      if (stored) {
        return { ...defaultSettings.titleFavicon, ...JSON.parse(stored) };
      }
    } catch (e) {
      console.error("Failed to load title/favicon settings:", e);
    }
    return defaultSettings.titleFavicon;
  }

  // Reactive settings state - reloads when localStorage changes
  let titleFaviconSettings = $state<TitleFaviconSettings>(
    loadTitleFaviconSettings()
  );

  // Listen for storage changes to update settings in real-time
  function handleStorageChange(e: StorageEvent) {
    if (e.key === SETTINGS_STORAGE_KEY) {
      titleFaviconSettings = loadTitleFaviconSettings();
    }
  }

  onMount(() => {
    realtimeStore.initialize();
    titleFaviconService.initialize();

    // Reload settings after hydration (SSR fix)
    titleFaviconSettings = loadTitleFaviconSettings();

    // Listen for localStorage changes (from settings page)
    if (browser) {
      window.addEventListener("storage", handleStorageChange);
    }
  });

  onDestroy(() => {
    realtimeStore.destroy();
    titleFaviconService.destroy();
    if (browser) {
      window.removeEventListener("storage", handleStorageChange);
    }
  });

  // Stale threshold in milliseconds (10 minutes)
  const STALE_THRESHOLD_MS = 10 * 60 * 1000;

  // Track current time for stale calculation - use shared store
  const now = $derived(realtimeStore.now);

  // Reactive updates when glucose changes or settings change
  const lastUpdated = $derived(realtimeStore.lastUpdated);
  const timeSinceReading = $derived(realtimeStore.timeSinceReading);

  const isDisconnected = $derived(!realtimeStore.isConnected);
  const isStale = $derived(now - lastUpdated > STALE_THRESHOLD_MS);

  $effect(() => {
    // Determine if we should update
    const enabled = titleFaviconSettings.enabled;
    const bg = realtimeStore.currentBG;

    // Explicit dependencies for visual updates
    const title = timeSinceReading;
    const delta = realtimeStore.bgDelta;
    const dir = realtimeStore.direction;

    if (enabled && bg > 0) {
      titleFaviconService.update(
        bg,
        dir,
        delta,
        titleFaviconSettings,
        defaultSettings.thresholds,
        isDisconnected,
        isStale,
        title
      );
    }
  });

  // Handle alarm events for flashing
  // When an alarm is active, start flashing with the alarm's visual settings
  $effect(() => {
    // For now, we can detect alarms by checking if BG is in alarm range
    // In the future, this should integrate with the alarm system's actual events
    const bg = realtimeStore.currentBG;
    if (
      bg &&
      titleFaviconSettings.enabled &&
      titleFaviconSettings.flashOnAlarm
    ) {
      const status = titleFaviconService.getGlucoseStatus(
        bg,
        defaultSettings.thresholds
      );
      if (status === "very-low" || status === "very-high") {
        // Start flashing with default alarm visual settings if not already flashing
        if (!titleFaviconService.isFlashing) {
          const alarmVisual: AlarmVisualSettings = {
            screenFlash: true,
            flashColor: status === "very-low" ? "#ef4444" : "#ef4444",
            flashIntervalMs: 500,
            persistentBanner: true,
            wakeScreen: true,
            showEmergencyContacts: false,
          };
          titleFaviconService.startFlashing(alarmVisual);
        }
      } else {
        // Stop flashing if no longer in alarm state
        if (titleFaviconService.isFlashing) {
          titleFaviconService.stopFlashing();
        }
      }
    }
  });
</script>

<Sidebar.Provider>
  <ModeWatcher />
  <AppSidebar user={data.user} />
  <MobileHeader />
  <Sidebar.Inset>
    <!-- Desktop header - hidden on mobile and on reports subpages (which have their own header) -->

    <main class="flex-1 overflow-auto">
      <svelte:boundary>
        {@render children()}

        {#snippet failed(e)}
          <Card.Root class="flex items-center justify-center h-full">
            <Card.Header>
              <Card.Title>Error</Card.Title>
            </Card.Header>
            <Card.Content
              class="text-destructive grid place-items-center h-full max-w-2xl"
            >
              {e instanceof Error ? e.message : JSON.stringify(e)}
            </Card.Content>
          </Card.Root>
        {/snippet}
      </svelte:boundary>
    </main>
  </Sidebar.Inset>
</Sidebar.Provider>
