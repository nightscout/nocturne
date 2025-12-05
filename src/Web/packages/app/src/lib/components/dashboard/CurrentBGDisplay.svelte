<script lang="ts">
  import { browser } from "$app/environment";
  import type { Entry } from "$lib/api";
  import { Badge } from "$lib/components/ui/badge";
  import { StatusPillBar } from "$lib/components/status-pills";
  import { getRealtimeStore } from "$lib/stores/realtime-store.svelte";
  import { Clock } from "lucide-svelte";

  interface ComponentProps {
    entries?: Entry[];
    currentBG?: number;
    direction?: string;
    bgDelta?: number;
    demoMode?: boolean;
    /**
     * Profile timezone (e.g., "Europe/Stockholm") - if different from local,
     * will show offset
     */
    profileTimezone?: string;
    /** Show status pills (COB, IOB, CAGE, SAGE, etc.) */
    showPills?: boolean;
  }

  let {
    currentBG,
    direction,
    bgDelta,
    demoMode,
    profileTimezone,
    showPills = true,
  }: ComponentProps = $props();

  const realtimeStore = getRealtimeStore();

  // Use realtime store values as fallback when props not provided
  const displayCurrentBG = $derived(currentBG ?? realtimeStore.currentBG);
  // Direction is derived but reserved for future use
  void (direction ?? realtimeStore.direction);
  const displayBgDelta = $derived(bgDelta ?? realtimeStore.bgDelta);
  const displayDemoMode = $derived(demoMode ?? realtimeStore.demoMode);

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

  // Format time in profile timezone if provided and different
  const profileTimeInfo = $derived.by(() => {
    if (!profileTimezone) return null;

    try {
      const localTz = Intl.DateTimeFormat().resolvedOptions().timeZone;
      if (localTz === profileTimezone) return null;

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

      return {
        time: profileTime,
        timezone:
          profileTimezone.split("/").pop()?.replace(/_/g, " ") ??
          profileTimezone,
        offset: offsetStr,
      };
    } catch {
      return null;
    }
  });

  // Get background color based on BG value
  const getBGColor = (bg: number) => {
    if (bg < 70) return "bg-red-500";
    if (bg < 80) return "bg-yellow-500";
    if (bg > 250) return "bg-red-500";
    if (bg > 180) return "bg-orange-500";
    return "bg-green-500";
  };
</script>

<div class="flex items-center justify-between">
  <div class="flex items-center gap-4">
    <h1 class="text-3xl font-bold">Nocturne</h1>
    {#if displayDemoMode}
      <Badge variant="secondary" class="flex items-center gap-1">
        <div class="w-2 h-2 bg-blue-500 rounded-full animate-pulse"></div>
        Demo Mode
      </Badge>
    {/if}
  </div>
  <div class="flex items-center gap-6">
    <!-- Current Time Display -->
    <div class="text-right">
      <div class="flex items-center gap-2 text-lg font-medium tabular-nums">
        <Clock class="h-4 w-4 text-muted-foreground" />
        {formattedLocalTime}
      </div>
      {#if profileTimeInfo}
        <div class="text-xs text-muted-foreground flex items-center gap-1">
          <span class="font-medium">{profileTimeInfo.timezone}:</span>
          <span class="tabular-nums">{profileTimeInfo.time}</span>
          <Badge variant="outline" class="text-[10px] px-1 py-0">
            {profileTimeInfo.offset}
          </Badge>
        </div>
      {/if}
    </div>

    <!-- BG Display -->
    <div class="flex items-center gap-2">
      <div class="relative">
        <div
          class="text-4xl font-bold {getBGColor(
            displayCurrentBG
          )} text-white px-4 py-2 rounded-lg"
        >
          {displayCurrentBG}
        </div>
      </div>
      <div class="text-center">
        <div class="text-2xl">
          <!-- Direction display placeholder -->
        </div>
        <div class="text-sm text-muted-foreground">
          {displayBgDelta > 0 ? "+" : ""}{displayBgDelta}
        </div>
      </div>
    </div>
  </div>
</div>

<!-- Status Pills Bar -->
{#if showPills}
  <div class="mt-4">
    <StatusPillBar
      iob={realtimeStore.pillsData.iob}
      cob={realtimeStore.pillsData.cob}
      cage={realtimeStore.pillsData.cage}
      sage={realtimeStore.pillsData.sage}
      basal={realtimeStore.pillsData.basal}
      loop={realtimeStore.pillsData.loop}
      units="mmol/L"
    />
  </div>
{/if}
