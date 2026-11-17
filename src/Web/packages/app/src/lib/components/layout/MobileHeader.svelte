<script lang="ts">
  import { tryGetRealtimeStore } from "#lib/stores/realtime-store.svelte.js";
  import { getDirectionInfo } from "#lib/utils/index.js";
  import {
    formatGlucoseValue,
    formatGlucoseDelta,
    relativeAge,
  } from "#lib/utils/formatting.js";
  import { glucoseUnits } from "#lib/stores/appearance-store.svelte.js";
  import { STALE_THRESHOLD_MS } from "#lib/constants/staleness.js";
  import { GlucoseValueIndicator } from "#lib/components/shared/index.js";
  import * as Sidebar from "#lib/components/ui/sidebar/index.js";
  import { displayedGlucose } from "#lib/stores/current-glucose-status.svelte.js";
  import { getGlucoseTileVariant } from "#lib/utils/glucose-status.js";

  const realtimeStore = tryGetRealtimeStore();

  const units = $derived(glucoseUnits.current);

  // Scroll tracking state
  let lastScrollY = $state(0);
  let isVisible = $state(true);
  let scrollThreshold = 10; // Minimum scroll amount to trigger hide/show

  // Get direction info for arrow display
  const glucose = displayedGlucose(realtimeStore);
  const directionInfo = $derived(getDirectionInfo(glucose.direction));

  // This header is the glucose surface on a phone unless the Current Glucose widget is showing —
  // CurrentBGDisplay hides itself below @md — so it carries the same stale/disconnected states.
  const rawCurrentBG = $derived(glucose.currentBG);
  const lastUpdated = $derived(realtimeStore?.lastUpdated ?? 0);
  const tileVariant = $derived(
    getGlucoseTileVariant(glucose.status)
  );
  const now = $derived(realtimeStore?.now ?? Date.now());
  const displayCurrentBG = $derived(formatGlucoseValue(rawCurrentBG, units));
  const isStale = $derived(now - lastUpdated > STALE_THRESHOLD_MS);
  const isDisconnected = $derived(realtimeStore?.connectionUnavailable ?? false);
  // No reading yet: show the skeleton rather than rendering the 0 sentinel as a
  // glucose value.
  const isLoading = $derived(rawCurrentBG <= 0);
  const statusText = $derived(
    isDisconnected
      ? "Connection error"
      : relativeAge(lastUpdated, { now, fallback: "Unknown" })
  );
  const statusTooltip = $derived(`Last reading: ${relativeAge(lastUpdated, { now, fallback: "Unknown" })}`);

  // Handle scroll events
  function handleScroll() {
    if (typeof window === "undefined") return;

    const currentScrollY = window.scrollY;
    const scrollDiff = currentScrollY - lastScrollY;

    // Show immediately when scrolling up
    if (scrollDiff < 0) {
      isVisible = true;
    }
    // Hide when scrolling down past threshold
    else if (scrollDiff > scrollThreshold && currentScrollY > 50) {
      isVisible = false;
    }

    lastScrollY = currentScrollY;
  }

  // Page-level sticky bars read this, through --app-sticky-top, to sit flush under the header as it slides.
  $effect(() => {
    const root = document.documentElement;
    root.style.setProperty("--mobile-header-offset", isVisible ? "3.5rem" : "0px");
    return () => root.style.removeProperty("--mobile-header-offset");
  });
</script>

<svelte:window onscroll={handleScroll} />

<!-- Mobile-only sticky header -->
<header
  class="md:hidden print:hidden fixed top-0 left-0 right-0 z-50 flex h-14 items-center justify-between gap-2 border-b border-border bg-background/95 backdrop-blur px-4 transition-transform duration-300"
  class:translate-y-0={isVisible}
  class:-translate-y-full={!isVisible}
>
  <!-- Sidebar trigger on the left -->
  <Sidebar.Trigger class="-ml-1" />

  <!-- Current BG display on the right -->
  {#if realtimeStore}
    <div class="flex items-center gap-2">
      <GlucoseValueIndicator
        displayValue={displayCurrentBG}
        variant={tileVariant}
        {isLoading}
        {isStale}
        {isDisconnected}
        {statusText}
        {statusTooltip}
        size="xs"
      />

      <!-- Direction arrow and delta. Hidden while stale: the trend describes a
           reading that is no longer current. -->
      {#if !isLoading && !isStale}
        <div class="flex flex-col items-center text-xs">
          <span class={directionInfo.css}>
            {#if directionInfo.icon}
              {@const Icon = directionInfo.icon}
              <Icon class="w-4 h-4" />
            {/if}
          </span>
          <span class="text-muted-foreground">
            {formatGlucoseDelta(glucose.bgDelta, units)}
          </span>
        </div>
      {/if}
    </div>
  {/if}
</header>

<!-- Spacer to prevent content from hiding behind fixed header on mobile -->
<div class="md:hidden h-14"></div>
