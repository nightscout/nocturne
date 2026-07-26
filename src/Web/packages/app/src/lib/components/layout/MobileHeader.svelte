<script lang="ts">
  import { tryGetRealtimeStore } from "$lib/stores/realtime-store.svelte";
  import { getDirectionInfo } from "$lib/utils";
  import {
    formatGlucoseValue,
    formatGlucoseDelta,
    minutesAgo,
  } from "$lib/utils/formatting";
  import { glucoseUnits } from "$lib/stores/appearance-store.svelte";
  import { STALE_THRESHOLD_MS } from "$lib/constants/staleness";
  import { GlucoseValueIndicator } from "$lib/components/shared";
  import * as Sidebar from "$lib/components/ui/sidebar";

  const realtimeStore = tryGetRealtimeStore();

  const units = $derived(glucoseUnits.current);

  // Scroll tracking state
  let lastScrollY = $state(0);
  let isVisible = $state(true);
  let scrollThreshold = 10; // Minimum scroll amount to trigger hide/show

  // Get direction info for arrow display
  const directionInfo = $derived(getDirectionInfo(realtimeStore?.direction ?? "NONE"));

  // This header is the only glucose surface on a phone — CurrentBGDisplay hides
  // itself below @md — so it carries the same stale/disconnected states.
  const rawCurrentBG = $derived(realtimeStore?.currentBG ?? 0);
  const lastUpdated = $derived(realtimeStore?.lastUpdated ?? 0);
  const now = $derived(realtimeStore?.now ?? Date.now());
  const displayCurrentBG = $derived(formatGlucoseValue(rawCurrentBG, units));
  const isStale = $derived(now - lastUpdated > STALE_THRESHOLD_MS);
  const isDisconnected = $derived(!(realtimeStore?.isConnected ?? false));
  // No reading yet: show the skeleton rather than rendering the 0 sentinel as a
  // glucose value.
  const isLoading = $derived(rawCurrentBG <= 0);
  const statusText = $derived(
    isDisconnected ? "Connection Error" : minutesAgo(lastUpdated, now)
  );
  const statusTooltip = $derived(`Last reading: ${minutesAgo(lastUpdated, now)}`);

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
        rawBgMgdl={rawCurrentBG}
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
            {formatGlucoseDelta(realtimeStore.bgDelta, units)}
          </span>
        </div>
      {/if}
    </div>
  {/if}
</header>

<!-- Spacer to prevent content from hiding behind fixed header on mobile -->
<div class="md:hidden h-14"></div>
