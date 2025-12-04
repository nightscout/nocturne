<script lang="ts">
  import { browser } from "$app/environment";
  import { goto } from "$app/navigation";
  import { page } from "$app/state";
  import { getRealtimeStore } from "$lib/stores/realtime-store.svelte";
  import { parseClockFace, type ClockConfig } from "$lib/clock-parser";
  import { Badge } from "$lib/components/ui/badge";
  import { Button } from "$lib/components/ui/button";
  import {
    Settings,
    ArrowLeft,
    Clock as ClockIcon,
    Globe,
  } from "lucide-svelte";

  const realtimeStore = getRealtimeStore();

  // Get face from route params
  const face = $derived(page.params.face);

  // Parse clock configuration from URL parameter
  const clockConfig = $derived<ClockConfig>(parseClockFace(face));

  // Get profile timezone from query params (e.g., ?tz=Europe/Stockholm)
  const profileTimezone = $derived(page.url.searchParams.get("tz"));

  // Get current glucose values from realtime store
  const currentBG = $derived(realtimeStore.currentBG);
  const bgDelta = $derived(realtimeStore.bgDelta);
  const direction = $derived(realtimeStore.direction);
  const lastUpdated = $derived(realtimeStore.lastUpdated);
  const demoMode = $derived(realtimeStore.demoMode);

  // Calculate staleness
  const isStale = $derived.by(() => {
    if (clockConfig.staleMinutes === 0) return false;
    const diff = Date.now() - lastUpdated;
    const mins = Math.floor(diff / 60000);
    return mins >= clockConfig.staleMinutes;
  });

  // Time since last reading
  const timeSince = $derived.by(() => {
    const diff = Date.now() - lastUpdated;
    const mins = Math.floor(diff / 60000);
    if (mins < 1) return "now";
    return `${mins}m`;
  });

  // Current time state (updated every second)
  let currentTime = $state(new Date());

  $effect(() => {
    if (!browser) return;
    const interval = setInterval(() => {
      currentTime = new Date();
    }, 1000);
    return () => clearInterval(interval);
  });

  // Format current time in local timezone
  const formattedLocalTime = $derived(
    currentTime.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })
  );

  // Format time in profile timezone if provided
  const profileTimeInfo = $derived.by(() => {
    if (!profileTimezone) return null;

    try {
      const localTz = Intl.DateTimeFormat().resolvedOptions().timeZone;
      const profileTime = currentTime.toLocaleTimeString([], {
        hour: "2-digit",
        minute: "2-digit",
        timeZone: profileTimezone,
      });

      // Calculate offset between timezones
      const localDate = new Date(
        currentTime.toLocaleString("en-US", { timeZone: localTz })
      );
      const profileDate = new Date(
        currentTime.toLocaleString("en-US", { timeZone: profileTimezone })
      );
      const diffHours = Math.round(
        (profileDate.getTime() - localDate.getTime()) / (1000 * 60 * 60)
      );
      const offsetStr = diffHours >= 0 ? `+${diffHours}h` : `${diffHours}h`;
      const isSameTimezone = localTz === profileTimezone;

      return {
        time: profileTime,
        localTime: formattedLocalTime,
        timezone:
          profileTimezone.split("/").pop()?.replace(/_/g, " ") ??
          profileTimezone,
        offset: offsetStr,
        isSameTimezone,
      };
    } catch {
      return null;
    }
  });

  // Get BG color class based on value
  const bgColorClass = $derived.by(() => {
    if (currentBG < 70) return "text-red-500";
    if (currentBG < 80) return "text-yellow-500";
    if (currentBG > 250) return "text-red-500";
    if (currentBG > 180) return "text-orange-500";
    return "text-green-500";
  });

  // Get BG background color for color mode
  const bgBgClass = $derived.by(() => {
    if (currentBG < 70) return "bg-red-500";
    if (currentBG < 80) return "bg-yellow-500";
    if (currentBG > 250) return "bg-red-500";
    if (currentBG > 180) return "bg-orange-500";
    return "bg-green-500";
  });

  // Get direction arrow
  const directionArrow = $derived.by(() => {
    switch (direction) {
      case "DoubleUp":
        return "⇈";
      case "SingleUp":
        return "↑";
      case "FortyFiveUp":
        return "↗";
      case "Flat":
        return "→";
      case "FortyFiveDown":
        return "↘";
      case "SingleDown":
        return "↓";
      case "DoubleDown":
        return "⇊";
      default:
        return "→";
    }
  });

  // Show time based on configuration
  const showTime = $derived(clockConfig.alwaysShowTime || isStale);

  // Calculate total content height to determine scale factor
  const totalElementSize = $derived.by(() => {
    let total = 0;
    for (const element of clockConfig.elements) {
      if (element.type === "nl") {
        total += 2;
      } else {
        total += element.size || getDefaultSize(element.type);
      }
    }
    return total + 10;
  });

  function getDefaultSize(type: string): number {
    switch (type) {
      case "sg":
        return 40;
      case "dt":
        return 14;
      case "ar":
        return 25;
      case "ag":
        return 6;
      case "time":
        return 8;
      default:
        return 10;
    }
  }

  // Scale factor to fit content in viewport
  const scaleFactor = $derived(Math.min(1, 85 / totalElementSize));

  // Calculate font size for an element (clamped to viewport)
  function getElementFontSize(size: number): string {
    const scaled = size * scaleFactor;
    return `clamp(1rem, ${scaled}vh, ${scaled}vh)`;
  }
</script>

<svelte:head>
  <title>Clock - {data.face}</title>
</svelte:head>

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
      onclick={() => goto("/clock/config")}
    >
      <Settings class="size-4" />
      Configure
    </Button>
  </div>
</div>

<!-- Clock Display -->
<div
  class="fixed inset-0 flex flex-col items-center justify-center overflow-hidden transition-colors duration-500
         {clockConfig.bgColor ? bgBgClass : 'bg-neutral-950'}"
>
  <div
    class="flex max-h-[90dvh] max-w-[95dvw] flex-col items-center justify-center gap-2"
  >
    {#each clockConfig.elements as element}
      {#if element.type === "nl"}
        <div class="h-2 w-full"></div>
      {:else if element.type === "sg"}
        <div
          class="font-bold tabular-nums leading-none transition-all duration-300
                 {clockConfig.bgColor ? 'text-white' : bgColorClass}
                 {isStale ? 'line-through opacity-60' : ''}"
          style:font-size={getElementFontSize(element.size || 40)}
        >
          {currentBG}
        </div>
      {:else if element.type === "dt"}
        <div
          class="font-medium tabular-nums opacity-90
                 {clockConfig.bgColor ? 'text-white' : bgColorClass}"
          style:font-size={getElementFontSize(element.size || 14)}
        >
          {bgDelta > 0 ? "+" : ""}{bgDelta} mg/dL
        </div>
      {:else if element.type === "ar"}
        <div
          class="leading-none transition-opacity duration-300
                 {clockConfig.bgColor ? 'text-white' : bgColorClass}
                 {isStale ? 'opacity-30' : ''}"
          style:font-size={getElementFontSize(element.size || 25)}
        >
          {directionArrow}
        </div>
      {:else if element.type === "ag"}
        <div
          class="font-medium opacity-70
                 {clockConfig.bgColor ? 'text-white' : bgColorClass}"
          style:font-size={getElementFontSize(element.size || 6)}
        >
          {timeSince} ago
        </div>
      {:else if element.type === "time"}
        <div
          class="font-medium tabular-nums opacity-80
                 {clockConfig.bgColor ? 'text-white' : bgColorClass}"
          style:font-size={getElementFontSize(element.size || 8)}
        >
          {formattedLocalTime}
        </div>
      {/if}
    {/each}

    <!-- Always show time if configured or if stale -->
    {#if showTime && !clockConfig.elements.some((e) => e.type === "time")}
      <div
        class="mt-4 text-center font-medium tabular-nums opacity-80
                  {clockConfig.bgColor ? 'text-white' : bgColorClass}"
      >
        {#if profileTimeInfo && !profileTimeInfo.isSameTimezone}
          <div class="flex flex-col items-center gap-1">
            <div
              class="flex items-center gap-2 text-2xl sm:text-3xl md:text-4xl"
            >
              <Globe class="size-6 opacity-60" />
              <span>{profileTimeInfo.time}</span>
              <span class="text-sm opacity-60 sm:text-base md:text-lg">
                {profileTimeInfo.timezone}
              </span>
            </div>
            <div
              class="flex items-center gap-2 text-sm opacity-60 sm:text-base md:text-xl"
            >
              <ClockIcon class="size-4" />
              <span>{profileTimeInfo.localTime}</span>
              <span class="text-xs sm:text-sm">
                Your time ({profileTimeInfo.offset})
              </span>
            </div>
          </div>
        {:else}
          <div
            class="flex items-center justify-center gap-2 text-xl sm:text-2xl md:text-3xl"
          >
            <ClockIcon class="size-6 opacity-60" />
            <span>{formattedLocalTime}</span>
          </div>
        {/if}
      </div>
    {/if}
  </div>

  <!-- Stale indicator -->
  {#if isStale}
    <div class="absolute bottom-8 left-1/2 -translate-x-1/2">
      <Badge
        variant="outline"
        class="px-4 py-2 text-base sm:text-lg
               {clockConfig.bgColor
          ? 'border-white text-white'
          : `border-current ${bgColorClass}`}"
      >
        Data is {timeSince} old
      </Badge>
    </div>
  {/if}

  <!-- Timezone indicator (when viewing remote profile) -->
  {#if profileTimeInfo && !profileTimeInfo.isSameTimezone && !showTime}
    <div class="absolute bottom-8 left-1/2 -translate-x-1/2">
      <Badge
        variant="outline"
        class="px-3 py-1 opacity-70
               {clockConfig.bgColor
          ? 'border-white text-white'
          : `border-current ${bgColorClass}`}"
      >
        <Globe class="mr-1 inline size-3" />
        {profileTimeInfo.timezone} ({profileTimeInfo.offset})
      </Badge>
    </div>
  {/if}
</div>
