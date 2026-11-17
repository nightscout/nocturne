<script lang="ts">
  import { browser } from "$app/env";
  import { goto } from "$app/navigation";
  import { resolve } from "$app/paths";
  import { page } from "$app/state";
  import { satisfiesScope } from "#lib/authorization/scopes.js";
  import { onMount } from "svelte";
  import { setClockGlucoseSource } from "#lib/stores/realtime-store.svelte.js";
  import { PublicClockStore } from "#lib/stores/public-clock-store.svelte.js";
  import { Badge } from "#lib/components/ui/badge/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import Settings from "@lucide/svelte/icons/settings";
  import ArrowLeft from "@lucide/svelte/icons/arrow-left";
  import ClockIcon from "@lucide/svelte/icons/clock";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import ClockFaceRenderer from "#lib/components/clock/ClockFaceRenderer.svelte";
  import ClockAlertBand from "#lib/components/clock/ClockAlertBand.svelte";
  import { formatClockTime } from "#lib/components/clock/clock-time.js";
  import {
    isClockReadingStale,
    readingAgeLabel,
  } from "#lib/components/clock/staleness.js";
  import type { ClockFaceConfig } from "#lib/api/index.js";
  import { getById as getClockFaceById } from "#lib/api/generated/clockFaces.generated.remote.js";

  // The route param identifies the clock and is the capability for its glucose.
  const id = page.params.id ?? "";

  const canManage = $derived(
    satisfiesScope(page.data.effectivePermissions ?? [], "sharing.manage"),
  );

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

  // Current time state
  let currentTime = $state(new Date());
  $effect(() => {
    if (!browser) return;
    const interval = setInterval(() => {
      currentTime = new Date();
    }, 1000);
    return () => clearInterval(interval);
  });

  // Reading age, driven by the ticker above so it advances while the CGM is silent.
  const isStale = $derived(isClockReadingStale(clockConfig?.settings?.staleMinutes, lastUpdated, currentTime.getTime()));

  const timeSince = $derived(lastUpdated === null
    ? ""
    : readingAgeLabel(lastUpdated, currentTime.getTime()));

  const clockTime = $derived(formatClockTime(currentTime, clockConfig?.settings?.timeFormat));

  // Show time based on configuration
  const showTime = $derived(clockConfig?.settings?.alwaysShowTime || isStale);

  // Touch has no hover, so a tap shows the navigation bar for a few seconds. While the last
  // pointer was a touch and the bar is hidden, it takes no taps. A key press hands the bar
  // back to the keyboard.
  const NAV_REVEAL_MS = 4000;
  let navRevealed = $state(false);
  let touchInput = $state(false);
  let navHideTimer: ReturnType<typeof setTimeout> | undefined;
  // A screen whose primary pointer is fine still lets a tap land on the hidden bar: the tap
  // that reveals it must not also press the button under it.
  let tapRevealedNav = false;

  function handlePointerDown(event: PointerEvent) {
    touchInput = event.pointerType !== "mouse";
    tapRevealedNav = touchInput && !navRevealed;
    if (!touchInput) return;
    navRevealed = true;
    clearTimeout(navHideTimer);
    navHideTimer = setTimeout(() => navRevealed = false, NAV_REVEAL_MS);
  }

  function handlePointerMove(event: PointerEvent) {
    if (event.pointerType === "mouse") touchInput = false;
  }

  function handleKeyDown() {
    touchInput = false;
    tapRevealedNav = false;
  }

  function handleNavClickCapture(event: MouseEvent) {
    if (!tapRevealedNav) return;
    tapRevealedNav = false;
    event.preventDefault();
    event.stopPropagation();
  }

  $effect(() => () => clearTimeout(navHideTimer));

  const showAlerts = $derived(clockConfig?.settings?.showAlerts ?? false);
  $effect(() => {
    clockStore.watchAlerts(showAlerts);
  });
</script>

<svelte:head>
  <title>Clock - Nocturne</title>
</svelte:head>

<!-- In the companion's transparent overlay window (`?embed=1`), clear the page background so the
     desktop shows through behind the clock face. -->
<svelte:body class:embed-transparent={embed}></svelte:body>

<svelte:window
  onpointerdown={handlePointerDown}
  onpointermove={handlePointerMove}
  onkeydown={handleKeyDown}
></svelte:window>

{#if loading}
  <div class="fixed inset-0 flex items-center justify-center bg-black">
    <Loader2 class="size-12 animate-spin text-white/50" />
  </div>
{:else if error}
  <div class="fixed inset-0 flex flex-col items-center justify-center gap-4 bg-black text-white">
    <ClockIcon class="size-12 text-white/30" />
    <p class="text-lg">{error}</p>

    <Button
      variant="outline"
      onclick={() => goto(resolve('clock'))}
    >
      <ArrowLeft class="mr-2 size-4" />
      Back to clock faces
    </Button>
  </div>
{:else if clockConfig}
  {#if !embed}
  <!-- Navigation overlay: shows on hover, or for a few seconds after a tap. On a coarse pointer
       each button's ::before widens its tap target to 44px. -->
  <div
    data-testid="clock-nav"
    data-revealed={navRevealed}
    inert={touchInput && !navRevealed}
    onclickcapture={handleNavClickCapture}
    class="fixed inset-x-0 top-0 z-50 flex items-center justify-between p-4
           bg-linear-to-b from-black/50 to-transparent
           transition-opacity duration-300 hover:opacity-100 focus-within:opacity-100
           {navRevealed ? 'opacity-100' : 'opacity-0 pointer-coarse:pointer-events-none'}"
    >
      <Button
        variant="overlay"
        size="sm"
        class="relative pointer-coarse:before:absolute pointer-coarse:before:-inset-1.5 pointer-coarse:before:content-['']"
        onclick={() => goto(resolve('clock'))}
      ><ArrowLeft class="size-4" />Back</Button>

      <div class="flex items-center gap-2">
        {#if demoMode}
        <Badge variant="overlay">
          Demo mode
        </Badge>
        {/if}
        {#if canManage}
          <Button
            variant="overlay"
            size="sm"
            class="relative pointer-coarse:before:absolute pointer-coarse:before:-inset-1.5 pointer-coarse:before:content-['']"
            onclick={() => goto(resolve("/(authenticated)/clock/config/[id]", { id }))}
        >
          <Settings class="size-4" />
          Edit
        </Button>
        {/if}
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

  <ClockAlertBand
    alerts={clockStore.alerts}
    now={currentTime.getTime()}
    onSeen={clockConfig.settings?.allowMarkSeen
      ? (excursionId) => clockStore.markSeen(excursionId)
      : undefined}
  />

  {#if !(clockConfig.settings?.screensaverMode ?? false)}
    <!-- Show time if configured or stale -->
    {#if showTime}
      <div class="fixed bottom-20 left-1/2 z-20 -translate-x-1/2">
        <div class="flex items-center gap-2 text-2xl text-white/80">
          <ClockIcon class="size-6" />
          {clockTime}
        </div>
      </div>
    {/if}

    <!-- Stale indicator -->
    {#if isStale}
      <div class="fixed bottom-8 left-1/2 z-20 -translate-x-1/2">
        <Badge variant="overlay" size="lg">
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
