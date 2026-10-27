<script lang="ts">
  import { Key } from "@nocturne/ui/keys";
  import { createRealtimeStore } from "#lib/stores/realtime-store.svelte.js";
  import {
    displayedGlucose,
    refreshSummaryOnNewReading,
  } from "#lib/stores/current-glucose-status.svelte.js";
  import { getGlucoseTileVariant } from "#lib/utils/glucose-status.js";
  import { createAuthStore } from "#lib/stores/auth-store.svelte.js";
  import { authInterceptorState } from "#lib/api/auth-interceptor.js";
  import { remoteErrorMessage } from "#lib/api/remote-error.js";
  import { onMount, onDestroy, type Snippet } from "svelte";
  import * as Sidebar from "#lib/components/ui/sidebar/index.js";
  import AppSidebar from "#lib/components/layout/AppSidebar.svelte";
  import MobileHeader from "#lib/components/layout/MobileHeader.svelte";
  import type { LayoutData } from "./$types";
  import { getTitleFaviconService } from "#lib/services/title-favicon-service.svelte.js";
  import { getDefaultSettings } from "#lib/components/settings/constants.js";
  import type { AlarmVisualSettings } from "#lib/types/alarm-profile.js";
  import type { TitleFaviconSettings } from "#lib/stores/serverSettings.js";
  import { browser, dev } from "$app/env";
  import { invalidate } from "$app/navigation";
  import { refreshOnVisible } from "#lib/utils/refresh-on-return.svelte.js";
  import { SHELL_DATA_KEY, SHELL_REVALIDATE_INTERVAL_MS } from "#lib/constants/shell-data.js";
  import { createShellRevalidator } from "#lib/utils/shell-session.js";
  import * as Card from "#lib/components/ui/card/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import AlertSurfaces from "#lib/components/alerts/AlertSurfaces.svelte";
  import { satisfiesScope } from "#lib/authorization/scopes.js";
  import DemoBanner from "#lib/components/layout/DemoBanner.svelte";
  import InitialReadsPrefetch from "#lib/components/layout/InitialReadsPrefetch.svelte";
  import GuestBanner from "#lib/components/layout/GuestBanner.svelte";
  import BackupSignInPrompt from "#lib/components/layout/BackupSignInPrompt.svelte";
  import MembershipRequestAutoSubmit from "#lib/components/members/MembershipRequestAutoSubmit.svelte";
  import { WELCOME_PARAM } from "#lib/components/members/welcome.js";
  import { page } from "$app/state";
  import { lazyComponent } from "#lib/utils/lazy-component.svelte.js";
  import { getEngineHost } from "water-ui";
  import { deferredWatercolour } from "#lib/watercolour-deferral.svelte.js";
  import { CoachMarkProvider } from "@nocturne/coach";
  import "@nocturne/coach/theme.css";
  import "../../styles/coach-theme-overrides.css";
  import { createCoachMarkAdapter } from "#lib/coach-marks/adapter.js";
  import { createCoachRouter } from "#lib/coach-marks/router.js";
  import { ONBOARDING_CORE_GATE, sequences } from "#lib/coach-marks/sequences.js";
  import { coachLabels } from "#lib/coach-marks/copy.svelte.js";
  import CoachParamHandler from "#lib/coach-marks/CoachParamHandler.svelte";
  import { STALE_THRESHOLD_MS } from "#lib/constants/staleness.js";
  import ChartPrintPatterns from "#lib/components/charts/print/ChartPrintPatterns.svelte";

  // LocalStorage key for title/favicon settings
  const SETTINGS_STORAGE_KEY = "nocturne-title-favicon-settings";

  interface Props {
    data: LayoutData;
    children: Snippet;
  }

  const { data, children }: Props = $props();

  // WebSocket config - defaults, can be overridden in production. A share host has no realtime feed.
  // svelte-ignore state_referenced_locally
  const config = {
    url: typeof window !== "undefined" ? window.location.origin : "",
    disabled: data.isShareHost === true,
    reconnectAttempts: Infinity,
    reconnectDelay: 5000,
    maxReconnectDelay: 30000,
    pingTimeout: 60000,
    pingInterval: 25000,
  };

  // A tenantless host leaves the tenant-scoped surfaces below unmounted; see
  // tenantless-navigation. Read once: the host cannot change without a fresh load.
  // svelte-ignore state_referenced_locally
  const tenantless: boolean = data.tenantless === true;

  const realtimeStore = createRealtimeStore(config);
  // Started while this layout initialises, ahead of the page beneath it: effects run only once
  // the whole tree has hydrated, so the effect below would issue these reads a long task later.
  // svelte-ignore state_referenced_locally
  if (browser && data.canViewRealtimeData) realtimeStore.initialize();
  refreshSummaryOnNewReading(
    () => realtimeStore.currentEntry?.mills,
    () => data.canViewRealtimeData
  );
  const authStore = createAuthStore(); // Initialize auth store in context
  refreshOnVisible(
    createShellRevalidator(
      () => data,
      () => void invalidate(SHELL_DATA_KEY)
    ),
    SHELL_REVALIDATE_INTERVAL_MS
  );

  // svelte-ignore state_referenced_locally
  let sessionSubjectId = data.user?.subjectId ?? null;
  $effect(() => {
    const subjectId = data.user?.subjectId ?? null;
    if (subjectId === sessionSubjectId) return;
    sessionSubjectId = subjectId;
    void authStore.loadSession();
  });

  // Suppress the auth interceptor's login redirect for guest and public
  // sessions — these have limited scopes so some endpoints return 401/403.
  $effect(() => {
    authInterceptorState.setGuestSession(data.isGuestSession || !data.isAuthenticated);
  });

  const commandPalette = lazyComponent(
    () => import("#lib/components/command-palette/CommandPalette.svelte"),
  );
  // Kept once loaded: the greeting strips the marker it was loaded for.
  const joinWelcome = lazyComponent(() => import("#lib/components/members/JoinWelcome.svelte"));
  $effect(() => {
    if (!tenantless && page.url.searchParams.has(WELCOME_PARAM)) joinWelcome.open = true;
  });
  let bannerStripHeight = $state(0);

  const coachMarkAdapter = createCoachMarkAdapter(tenantless);
  const coachRouter = createCoachRouter();

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

  function handleCommandPaletteKeydown(e: KeyboardEvent) {
    if ((e.metaKey || e.ctrlKey) && e.key === Key.K) {
      e.preventDefault();
      commandPalette.open = !commandPalette.open;
    }
  }

  // Listen for storage changes to update settings in real-time
  function handleStorageChange(e: StorageEvent) {
    if (e.key === SETTINGS_STORAGE_KEY) {
      titleFaviconSettings = loadTitleFaviconSettings();
    }
  }

  // Initialize realtime and title/favicon services for users authorized to
  // view glucose data (authenticated with read permissions, or public site visitors).
  let realtimeRunning = false;
  $effect(() => {
    if (data.canViewRealtimeData) {
      realtimeRunning = true;
      realtimeStore.initialize();
      titleFaviconService.initialize();
    } else if (realtimeRunning) {
      realtimeRunning = false;
      realtimeStore.destroy();
      titleFaviconService.destroy();
    }
  });

  onMount(() => {
    // Reload settings after hydration (SSR fix)
    titleFaviconSettings = loadTitleFaviconSettings();

    // Listen for localStorage changes (from settings page)
    if (browser) {
      window.addEventListener("storage", handleStorageChange);
      window.addEventListener("keydown", handleCommandPaletteKeydown);
    }
  });

  onDestroy(() => {
    realtimeStore.destroy();
    titleFaviconService.destroy();
    if (browser) {
      window.removeEventListener("storage", handleStorageChange);
      window.removeEventListener("keydown", handleCommandPaletteKeydown);
    }
  });

  /**
   * The first engine acquire blocks the main thread for a few hundred ms while the wasm loads.
   * Paid on a pointer-enter it freezes the transition that pointer started, and a paint drop's
   * backend is settled within 90 ms of the hover or it falls back to a still. It waits for the
   * current reading as well as {@link deferredWatercolour}, then for idle.
   *
   * Resolves false where there is no GPU, which needs no handling: that is the case the baked
   * and static paths exist for. Artwork that needs the engine sooner boots it on demand; the
   * unauthenticated pages render theirs on mount, so a warm-up there would gain nothing.
   */
  const engineWarmDeferral = deferredWatercolour();
  let engineWarmScheduled = false;
  $effect(() => {
    if (engineWarmScheduled || !engineWarmDeferral.due) return;
    if (data.canViewRealtimeData && !realtimeStore.currentReadingLoaded) return;
    engineWarmScheduled = true;
    void getEngineHost().warmWhenIdle();
  });

  // Track current time for stale calculation - use shared store
  const now = $derived(realtimeStore.now);

  // Reactive updates when glucose changes or settings change
  const lastUpdated = $derived(realtimeStore.lastUpdated);
  const timeSinceReading = $derived(realtimeStore.timeSinceReading);

  const isDisconnected = $derived(realtimeStore.connectionUnavailable);
  const isStale = $derived(now - lastUpdated > STALE_THRESHOLD_MS);
  const glucose = displayedGlucose(realtimeStore);

  $effect(() => {
    // Determine if we should update
    const enabled = titleFaviconSettings.enabled;
    const bg = glucose.currentBG;

    // Explicit dependencies for visual updates
    const title = timeSinceReading;
    const delta = glucose.bgDelta;
    const dir = glucose.direction;
    const variant = getGlucoseTileVariant(glucose.status);

    if (enabled && bg > 0) {
      titleFaviconService.update(
        bg,
        dir,
        delta,
        titleFaviconSettings,
        variant,
        isDisconnected,
        isStale,
        title
      );
    }
  });

  const alarmVisual: AlarmVisualSettings = {
    screenFlash: true,
    flashColor: "",
    flashIntervalMs: 1000,
    persistentBanner: true,
    wakeScreen: true,
    showEmergencyContacts: false,
  };

  $effect(() => {
    const bg = glucose.currentBG;
    if (
      bg &&
      titleFaviconSettings.enabled &&
      titleFaviconSettings.flashOnAlarm
    ) {
      titleFaviconService.syncAlarmFlash(glucose.status, alarmVisual);
    }
  });
</script>

<InitialReadsPrefetch enabled={data.canViewRealtimeData} />

<CoachMarkProvider
  adapter={coachMarkAdapter}
  labels={coachLabels()}
  {sequences}
  gates={{ [ONBOARDING_CORE_GATE]: data.onboardingComplete }}
  router={coachRouter}
>
  <CoachParamHandler />
  <ChartPrintPatterns />
  <Sidebar.Provider>
    <AppSidebar user={data.user} isPlatformAdmin={data.isPlatformAdmin} isPlatformAccessGrant={data.isPlatformAccessGrant} isGuestSession={data.isGuestSession} currentSlug={data.tenantSlug} baseDomain={data.baseDomain} tenantless={data.tenantless} tenantTargets={data.tenantTargets} />
    <!-- --app-sticky-top is the top of the usable viewport below the app chrome (the visible
         MobileHeader plus the banner strip); page-level sticky bars pin to it with the
         header's 300ms transition. -->
    <Sidebar.Inset
      class="min-w-0 [--app-sticky-top:calc(var(--mobile-header-offset,0px)_+_var(--app-banner-height))] md:[--app-sticky-top:var(--app-banner-height)]"
      style="--app-banner-height: {bannerStripHeight}px"
    >
      <MobileHeader />
      <div
        class="@container sticky top-(--mobile-header-offset,0px) z-40 transition-all duration-300 md:top-0"
        bind:offsetHeight={bannerStripHeight}
      >
          {#if data.isDemo}
            <DemoBanner nextResetAt={data.nextResetAt} />
          {/if}
          {#if data.isGuestSession && data.guestExpiresAt}
            <GuestBanner expiresAt={data.guestExpiresAt} />
          {/if}
          {#if !tenantless && data.user && !data.isGuestSession && !data.isDemo}
            <BackupSignInPrompt show={data.showBackupSignInPrompt} />
          {/if}
      </div>
      {#if !tenantless}
        <MembershipRequestAutoSubmit
          isAuthenticated={!!data.user}
          isGuestSession={data.isGuestSession}
        />
        {#if satisfiesScope(data.effectivePermissions ?? [], "alerts.read")}
          <AlertSurfaces />
        {/if}
      {/if}
      <main class="@container/main flex-1 overflow-x-clip">
        {#if joinWelcome.component}
          <joinWelcome.component />
        {/if}
        <svelte:boundary>
          {@render children()}

          {#snippet failed(e, reset)}
            {@const message = e instanceof Error ? e.message : typeof e === 'string' ? e : remoteErrorMessage(e, 'An unexpected error occurred')}
            {@const stack = dev && e instanceof Error ? e.stack : undefined}
            <Card.Root class="mx-auto mt-10 max-w-2xl">
              <Card.Header>
                <Card.Title>An error occurred</Card.Title>
              </Card.Header>
              <Card.Content class="space-y-4">
                <p class="text-destructive">{message}</p>
                {#if stack}
                  <pre class="max-h-48 overflow-auto rounded bg-muted p-3 text-xs text-muted-foreground">{stack}</pre>
                {/if}
                <div class="flex gap-2">
                  <Button variant="outline" onclick={reset}>Retry</Button>
                  <Button variant="ghost" href="/">Go home</Button>
                </div>
              </Card.Content>
            </Card.Root>
          {/snippet}
        </svelte:boundary>
      </main>
    </Sidebar.Inset>
  </Sidebar.Provider>
  {#if commandPalette.component}
    <commandPalette.component bind:open={commandPalette.open} {tenantless} />
  {/if}
</CoachMarkProvider>
