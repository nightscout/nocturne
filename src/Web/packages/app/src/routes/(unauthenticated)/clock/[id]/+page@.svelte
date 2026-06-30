<script lang="ts">
  import { browser } from "$app/environment";
  import { goto } from "$app/navigation";
  import { page } from "$app/state";
  import { onMount } from "svelte";
  import { setClockGlucoseSource } from "$lib/stores/realtime-store.svelte";
  import { PublicClockStore } from "$lib/stores/public-clock-store.svelte";
  import { Badge } from "$lib/components/ui/badge";
  import { Button } from "$lib/components/ui/button";
  import {
    Settings,
    ArrowLeft,
    Clock as ClockIcon,
    Loader2,
  } from "lucide-svelte";
  import ClockFaceRenderer from "$lib/components/clock/ClockFaceRenderer.svelte";
  import type { ClockFaceConfig } from "$lib/api";
  import { getById as getClockFaceById } from "$api/generated/clockFaces.generated.remote";

  // The route param identifies the clock and is the capability for its glucose.
  const id = page.params.id ?? "";

  // `?embed=1` hosts this page inside the desktop companion's floating window, where the in-app
  // navigation (which links back to the authenticated /clock area) doesn't apply.
  const embed = $derived(page.url.searchParams.get("embed") === "1");

  // This is the anonymous public view: there is no realtime store in context here.
  // Poll the capability-scoped, glucose-only endpoint and expose it to
  // ClockFaceRenderer via the shared ClockGlucoseSource context.
  const clockStore = new PublicClockStore(id);
  setClockGlucoseSource(clockStore);

  onMount(() => {
    clockStore.start();
    return () => clockStore.stop();
  });

  const lastUpdated = $derived(clockStore.lastUpdated);
  const demoMode = $derived(clockStore.demoMode);

  // Clock face config (loaded from API)
  let clockConfig = $state<ClockFaceConfig | null>(null);
  let loading = $state(true);
  let error = $state<string | null>(null);

  // Load clock face config from API
  $effect(() => {
    if (!browser || !id) return;

    loading = true;
    error = null;

    getClockFaceById(id)
      .then((clockFace) => {
        clockConfig = clockFace.config ?? null;
        if (!clockConfig) {
          error = "Clock face has no configuration";
        }
      })
      .catch((err) => {
        console.error("Failed to load clock face:", err);
        error = "Clock face not found";
      })
      .finally(() => {
        loading = false;
      });
  });

  // Calculate staleness
  const isStale = $derived.by(() => {
    if (!clockConfig?.settings?.staleMinutes) return false;
    if (clockConfig.settings.staleMinutes === 0) return false;
    const diff = Date.now() - lastUpdated;
    const mins = Math.floor(diff / 60000);
    return mins >= clockConfig.settings.staleMinutes;
  });

  // Time since last reading
  const timeSince = $derived.by(() => {
    const diff = Date.now() - lastUpdated;
    const mins = Math.floor(diff / 60000);
    if (mins < 1) return "now";
    return `${mins}m`;
  });

  // Current time state
  let currentTime = $state(new Date());
  $effect(() => {
    if (!browser) return;
    const interval = setInterval(() => {
      currentTime = new Date();
    }, 1000);
    return () => clearInterval(interval);
  });

  // Format time based on 12h/24h preference
  function formatTime(): string {
    return currentTime.toLocaleTimeString([], {
      hour: "2-digit",
      minute: "2-digit",
      hour12: true,
    });
  }

  // Show time based on configuration
  const showTime = $derived(clockConfig?.settings?.alwaysShowTime || isStale);
</script>

<svelte:head>
  <title>Clock - Nocturne</title>
</svelte:head>

<!-- In the companion's transparent overlay window (`?embed=1`), clear the page background so the
     desktop shows through behind the clock face. -->
<svelte:body class:embed-transparent={embed} />

{#if loading}
  <div class="fixed inset-0 flex items-center justify-center bg-neutral-950">
    <Loader2 class="size-12 animate-spin text-white/50" />
  </div>
{:else if error}
  <div class="fixed inset-0 flex flex-col items-center justify-center gap-4 bg-neutral-950 text-white">
    <ClockIcon class="size-12 text-white/30" />
    <p class="text-lg">{error}</p>
    <Button variant="outline" onclick={() => goto("/clock")}>
      <ArrowLeft class="mr-2 size-4" />
      Back to Clock Faces
    </Button>
  </div>
{:else if clockConfig}
  {#if !embed}
  <!-- Navigation overlay (shows on hover) -->
  <div
    class="fixed inset-x-0 top-0 z-50 flex items-center justify-between p-4
           bg-linear-to-b from-black/50 to-transparent
           opacity-0 transition-opacity duration-300 hover:opacity-100"
  >
    <Button
      variant="ghost"
      size="sm"
      class="gap-2 text-white/80 hover:text-white"
      onclick={() => goto("/clock")}
    >
      <ArrowLeft class="size-4" />
      Back
    </Button>
    <div class="flex items-center gap-2">
      {#if demoMode}
        <Badge variant="outline" class="border-white/30 text-white/80">
          Demo Mode
        </Badge>
      {/if}
      <Button
        variant="ghost"
        size="sm"
        class="gap-2 text-white/80 hover:text-white"
        onclick={() => goto(`/clock/config/${id}`)}
      >
        <Settings class="size-4" />
        Edit
      </Button>
    </div>
  </div>
  {/if}

  <!-- Clock Display -->
  <ClockFaceRenderer
    config={clockConfig}
    screensaver={clockConfig.settings?.screensaverMode ?? false}
    showCharts={false}
    loadTrackerDefinitions={false}
    class="fixed inset-0 h-screen w-screen transition-colors duration-500"
  />

  {#if !(clockConfig.settings?.screensaverMode ?? false)}
    <!-- Show time if configured or stale -->
    {#if showTime}
      <div class="fixed bottom-20 left-1/2 z-20 -translate-x-1/2">
        <div class="flex items-center gap-2 text-2xl text-white/80">
          <ClockIcon class="size-6" />
          {formatTime()}
        </div>
      </div>
    {/if}

    <!-- Stale indicator -->
    {#if isStale}
      <div class="fixed bottom-8 left-1/2 z-20 -translate-x-1/2">
        <Badge variant="outline" class="border-white/50 px-4 py-2 text-white">
          Data is {timeSince} old
        </Badge>
      </div>
    {/if}
  {/if}
{/if}

<style>
  /* A full-screen clock display never scrolls, in any mode. */
  :global(html),
  :global(body) {
    overflow: hidden;
  }

  /* Embed-only (class toggled on <body> above): let the desktop show through the companion's
     transparent overlay window. Unlayered, so it beats the `@layer base` body background. */
  :global(body.embed-transparent) {
    background: transparent;
  }
</style>
