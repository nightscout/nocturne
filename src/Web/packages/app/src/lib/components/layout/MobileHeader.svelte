<script lang="ts">
  import { tryGetRealtimeStore } from "$lib/stores/realtime-store.svelte";
  import { getDirectionInfo } from "$lib/utils";
  import { formatGlucoseDelta } from "$lib/utils/formatting";
  import { glucoseUnits } from "$lib/stores/appearance-store.svelte";
  import * as Sidebar from "$lib/components/ui/sidebar";

  const realtimeStore = tryGetRealtimeStore();

  const units = $derived(glucoseUnits.current);

  // Scroll tracking state
  let lastScrollY = $state(0);
  let isVisible = $state(true);
  let scrollThreshold = 10; // Minimum scroll amount to trigger hide/show

  // Get direction info for arrow display
  const directionInfo = $derived(getDirectionInfo(realtimeStore?.direction ?? "NONE"));

  // Get background color based on BG value
  function getBGColor(bg: number): string {
    if (bg < 70) return "bg-red-500";
    if (bg < 80) return "bg-yellow-500";
    if (bg > 250) return "bg-red-500";
    if (bg > 180) return "bg-orange-500";
    return "bg-green-500";
  }

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
  class="md:hidden fixed top-0 left-0 right-0 z-50 flex h-14 items-center justify-between gap-2 border-b border-border bg-background/95 backdrop-blur px-4 transition-transform duration-300"
  class:translate-y-0={isVisible}
  class:-translate-y-full={!isVisible}
>
  <!-- Sidebar trigger on the left -->
  <Sidebar.Trigger class="-ml-1" />

  <!-- Current BG display on the right -->
  {#if realtimeStore && realtimeStore.currentBG > 0}
    <div class="flex items-center gap-2">
      <!-- BG Value -->
      <div
        class="text-xl font-bold {getBGColor(
          realtimeStore.currentBG
        )} text-white px-3 py-1 rounded-md"
      >
        {realtimeStore.currentBG}
      </div>

      <!-- Direction arrow and delta -->
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
    </div>
  {:else}
    <div class="text-sm text-muted-foreground">Loading...</div>
  {/if}
</header>

<!-- Spacer to prevent content from hiding behind fixed header on mobile -->
<div class="md:hidden h-14"></div>
